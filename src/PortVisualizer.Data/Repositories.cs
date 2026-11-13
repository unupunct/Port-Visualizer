using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using PortVisualizer.Core.Alerts;
using PortVisualizer.Core.Models;
using PortVisualizer.Core.Monitoring;
using PortVisualizer.Core.Settings;

namespace PortVisualizer.Data;

public sealed class HistoryQuery
{
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }
    public string? Process { get; set; }
    public string? Ip { get; set; }
    public int? Port { get; set; }
    public HashSet<ChangeType> Types { get; set; } = new();
    public int Limit { get; set; } = 1000;
    public int Offset { get; set; }
}

public sealed record TimelineBucket(DateTime StartUtc, int Opened, int Closed, int Other);

public sealed class HistoryRepository
{
    private readonly PortVisualizerDatabase _db;
    private readonly Dictionary<string, long> _processIds = new();

    public HistoryRepository(PortVisualizerDatabase db) => _db = db;

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    private static DateTime ParseIso(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>
    /// Persists a batch of changes in one transaction: process identities, history events (optional) and the
    /// CurrentConnections delta. Only changes are written; never a full snapshot.
    /// </summary>
    public void WriteBatch(IReadOnlyList<TrackedChange> changes, bool recordHistory = true, bool recordPaths = true)
    {
        if (changes.Count == 0) return;
        using var c = _db.Open();
        using var tx = c.BeginTransaction();

        using var insert = c.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO ConnectionEvents (TimestampUtc, EventType, ProcessId, ProcessRef, ProcessName, Protocol, LocalAddress, LocalPort, RemoteAddress, RemotePort, ConnectionState, PreviousState)
            VALUES ($ts,$type,$pid,$pref,$pname,$proto,$la,$lp,$ra,$rp,$state,$prev)
            """;
        foreach (var n in new[] { "$ts", "$type", "$pid", "$pref", "$pname", "$proto", "$la", "$lp", "$ra", "$rp", "$state", "$prev" })
            insert.Parameters.Add(new SqliteParameter(n, null));

        using var upsert = c.CreateCommand();
        upsert.Transaction = tx;
        upsert.CommandText = """
            INSERT INTO CurrentConnections (ConnectionKey, ProcessId, Protocol, LocalAddress, LocalPort, RemoteAddress, RemotePort, ConnectionState, FirstObservedUtc, LastObservedUtc)
            VALUES ($k,$pid,$proto,$la,$lp,$ra,$rp,$state,$first,$last)
            ON CONFLICT(ConnectionKey) DO UPDATE SET ProcessId=$pid, ConnectionState=$state, LastObservedUtc=$last
            """;
        foreach (var n in new[] { "$k", "$pid", "$proto", "$la", "$lp", "$ra", "$rp", "$state", "$first", "$last" })
            upsert.Parameters.Add(new SqliteParameter(n, null));

        using var delete = c.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = "DELETE FROM CurrentConnections WHERE ConnectionKey=$k";
        delete.Parameters.Add(new SqliteParameter("$k", null));

        foreach (var ch in changes)
        {
            var e = ch.Event;
            var t = ch.Connection;
            var procRef = UpsertProcess(c, tx, e, recordPaths);

            if (recordHistory)
            {
                Set(insert, "$ts", Iso(e.TimestampUtc)); Set(insert, "$type", e.Type.ToString()); Set(insert, "$pid", e.Pid);
                Set(insert, "$pref", procRef); Set(insert, "$pname", e.ProcessName); Set(insert, "$proto", e.Protocol.ToString());
                Set(insert, "$la", e.LocalAddress); Set(insert, "$lp", e.LocalPort); Set(insert, "$ra", e.RemoteAddress);
                Set(insert, "$rp", e.RemotePort); Set(insert, "$state", e.State.ToString()); Set(insert, "$prev", e.PreviousState?.ToString());
                insert.ExecuteNonQuery();
            }

            switch (e.Type)
            {
                case ChangeType.NewConnection or ChangeType.NewUdpEndpoint or ChangeType.NewListeningPort or ChangeType.StateChanged:
                    Set(upsert, "$k", e.ConnectionKey); Set(upsert, "$pid", e.Pid); Set(upsert, "$proto", e.Protocol.ToString());
                    Set(upsert, "$la", e.LocalAddress); Set(upsert, "$lp", e.LocalPort); Set(upsert, "$ra", e.RemoteAddress);
                    Set(upsert, "$rp", e.RemotePort); Set(upsert, "$state", e.State.ToString());
                    Set(upsert, "$first", Iso(t.FirstObservedUtc)); Set(upsert, "$last", Iso(e.TimestampUtc));
                    upsert.ExecuteNonQuery();
                    break;
                case ChangeType.ConnectionClosed or ChangeType.UdpEndpointRemoved or ChangeType.ListeningPortClosed:
                    Set(delete, "$k", e.ConnectionKey);
                    delete.ExecuteNonQuery();
                    break;
            }
        }
        tx.Commit();
    }

    private static void Set(SqliteCommand cmd, string name, object? value) => cmd.Parameters[name].Value = value ?? DBNull.Value;

    private long UpsertProcess(SqliteConnection c, SqliteTransaction tx, ConnectionEvent e, bool recordPaths)
    {
        var key = e.ProcessKey;
        var now = Iso(e.TimestampUtc);
        if (_processIds.TryGetValue(key, out var id))
        {
            using var up = c.CreateCommand();
            up.Transaction = tx;
            up.CommandText = "UPDATE Processes SET LastSeen=$t WHERE Id=$id";
            up.Parameters.AddWithValue("$t", now); up.Parameters.AddWithValue("$id", id);
            if (up.ExecuteNonQuery() > 0) return id;
            _processIds.Remove(key); // row was cleaned up
        }
        var start = e.ProcessStartUtc is { } s ? Iso(s) : null;
        using var sel = c.CreateCommand();
        sel.Transaction = tx;
        sel.CommandText = "SELECT Id FROM Processes WHERE ProcessId=$pid AND IFNULL(StartTimeUtc,'')=IFNULL($start,'')";
        sel.Parameters.AddWithValue("$pid", e.Pid); sel.Parameters.AddWithValue("$start", (object?)start ?? DBNull.Value);
        var found = sel.ExecuteScalar();
        if (found is not null)
        {
            id = Convert.ToInt64(found);
            using var up = c.CreateCommand();
            up.Transaction = tx;
            up.CommandText = "UPDATE Processes SET LastSeen=$t WHERE Id=$id";
            up.Parameters.AddWithValue("$t", now); up.Parameters.AddWithValue("$id", id);
            up.ExecuteNonQuery();
        }
        else
        {
            using var ins = c.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO Processes (ProcessId, ProcessName, ExecutablePath, StartTimeUtc, FirstSeen, LastSeen)
                VALUES ($pid,$name,$path,$start,$t,$t); SELECT last_insert_rowid();
                """;
            ins.Parameters.AddWithValue("$pid", e.Pid); ins.Parameters.AddWithValue("$name", e.ProcessName);
            ins.Parameters.AddWithValue("$path", recordPaths && e.ProcessPath is not null ? e.ProcessPath : DBNull.Value);
            ins.Parameters.AddWithValue("$start", (object?)start ?? DBNull.Value); ins.Parameters.AddWithValue("$t", now);
            id = Convert.ToInt64(ins.ExecuteScalar());
        }
        _processIds[key] = id;
        return id;
    }

    private static (string Where, List<SqliteParameter> Params) BuildWhere(HistoryQuery q)
    {
        var w = new List<string>();
        var p = new List<SqliteParameter>();
        if (q.FromUtc is { } f) { w.Add("TimestampUtc >= $from"); p.Add(new("$from", Iso(f))); }
        if (q.ToUtc is { } t) { w.Add("TimestampUtc <= $to"); p.Add(new("$to", Iso(t))); }
        if (!string.IsNullOrWhiteSpace(q.Process)) { w.Add("ProcessName LIKE $proc ESCAPE '\\'"); p.Add(new("$proc", "%" + Like(q.Process.Trim()) + "%")); }
        if (!string.IsNullOrWhiteSpace(q.Ip)) { w.Add("(RemoteAddress LIKE $ip ESCAPE '\\' OR LocalAddress LIKE $ip ESCAPE '\\')"); p.Add(new("$ip", "%" + Like(q.Ip.Trim()) + "%")); }
        if (q.Port is { } port) { w.Add("(LocalPort=$port OR RemotePort=$port)"); p.Add(new("$port", port)); }
        if (q.Types.Count > 0)
        {
            var names = q.Types.Select((ty, i) => ($"$t{i}", ty.ToString())).ToList();
            w.Add("EventType IN (" + string.Join(",", names.Select(n => n.Item1)) + ")");
            p.AddRange(names.Select(n => new SqliteParameter(n.Item1, n.Item2)));
        }
        return (w.Count == 0 ? "" : " WHERE " + string.Join(" AND ", w), p);
    }

    private static string Like(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public List<ConnectionEvent> Query(HistoryQuery q)
    {
        var (where, ps) = BuildWhere(q);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.Id, e.TimestampUtc, e.EventType, e.ProcessId, e.ProcessName, p.ExecutablePath, p.StartTimeUtc, e.Protocol,
                   e.LocalAddress, e.LocalPort, e.RemoteAddress, e.RemotePort, e.ConnectionState, e.PreviousState
            FROM (SELECT * FROM ConnectionEvents{where} ORDER BY TimestampUtc DESC, Id DESC LIMIT $limit OFFSET $offset) e
            LEFT JOIN Processes p ON p.Id = e.ProcessRef
            ORDER BY e.TimestampUtc DESC, e.Id DESC
            """;
        foreach (var p in ps) cmd.Parameters.Add(p);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(q.Limit, 1, 100000));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, q.Offset));
        var list = new List<ConnectionEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var state = Enum.Parse<TcpState>(r.GetString(12));
            list.Add(new ConnectionEvent(ParseIso(r.GetString(1)), Enum.Parse<ChangeType>(r.GetString(2)), r.GetInt32(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), $"{r.GetInt32(3)}", r.IsDBNull(6) ? null : ParseIso(r.GetString(6)),
                Enum.Parse<Protocol>(r.GetString(7)), r.GetString(8), r.GetInt32(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetInt32(11),
                state, r.IsDBNull(13) ? null : Enum.Parse<TcpState>(r.GetString(13)), "") { Id = r.GetInt64(0) });
        }
        return list;
    }

    public long Count(HistoryQuery q)
    {
        var (where, ps) = BuildWhere(q);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ConnectionEvents" + where;
        foreach (var p in ps) cmd.Parameters.Add(p);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public List<TimelineBucket> Timeline(DateTime fromUtc, DateTime toUtc, int bucketMinutes)
    {
        bucketMinutes = Math.Max(1, bucketMinutes);
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT CAST(strftime('%s', TimestampUtc) AS INTEGER) / $b AS bucket,
                   SUM(CASE WHEN EventType IN ('NewConnection','NewUdpEndpoint','NewListeningPort') THEN 1 ELSE 0 END),
                   SUM(CASE WHEN EventType IN ('ConnectionClosed','UdpEndpointRemoved','ListeningPortClosed') THEN 1 ELSE 0 END),
                   SUM(CASE WHEN EventType NOT IN ('NewConnection','NewUdpEndpoint','NewListeningPort','ConnectionClosed','UdpEndpointRemoved','ListeningPortClosed') THEN 1 ELSE 0 END)
            FROM ConnectionEvents WHERE TimestampUtc >= $from AND TimestampUtc <= $to
            GROUP BY bucket ORDER BY bucket
            """;
        cmd.Parameters.AddWithValue("$b", bucketMinutes * 60);
        cmd.Parameters.AddWithValue("$from", Iso(fromUtc)); cmd.Parameters.AddWithValue("$to", Iso(toUtc));
        var list = new List<TimelineBucket>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TimelineBucket(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0) * bucketMinutes * 60).UtcDateTime, r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    public long CountNewConnectionsSince(DateTime sinceUtc)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ConnectionEvents WHERE TimestampUtc >= $t AND EventType IN ('NewConnection','NewUdpEndpoint')";
        cmd.Parameters.AddWithValue("$t", Iso(sinceUtc));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Deletes events older than the retention window. retentionDays &lt;= 0 means unlimited. Returns rows removed.</summary>
    public int ApplyRetention(int retentionDays, DateTime nowUtc)
    {
        if (retentionDays <= 0) return 0;
        var cutoff = Iso(nowUtc.AddDays(-retentionDays));
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM ConnectionEvents WHERE TimestampUtc < $c";
        cmd.Parameters.AddWithValue("$c", cutoff);
        var removed = cmd.ExecuteNonQuery();
        cmd.CommandText = "DELETE FROM Processes WHERE LastSeen < $c AND Id NOT IN (SELECT DISTINCT ProcessRef FROM ConnectionEvents WHERE ProcessRef IS NOT NULL)";
        cmd.ExecuteNonQuery();
        tx.Commit();
        _processIds.Clear();
        return removed;
    }

    public void ClearHistory()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM ConnectionEvents; DELETE FROM Processes;";
        cmd.ExecuteNonQuery();
        _processIds.Clear();
        using var vac = c.CreateCommand();
        vac.CommandText = "VACUUM";
        vac.ExecuteNonQuery();
    }

    public void ClearCurrentConnections()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM CurrentConnections";
        cmd.ExecuteNonQuery();
    }

    public long CountCurrentConnections()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM CurrentConnections";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public long CountProcesses()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Processes";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void InsertRawEventForTest(ConnectionEvent e)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ConnectionEvents (TimestampUtc, EventType, ProcessId, ProcessName, Protocol, LocalAddress, LocalPort, RemoteAddress, RemotePort, ConnectionState)
            VALUES ($ts,$type,$pid,$pname,$proto,$la,$lp,$ra,$rp,$state)
            """;
        cmd.Parameters.AddWithValue("$ts", Iso(e.TimestampUtc)); cmd.Parameters.AddWithValue("$type", e.Type.ToString());
        cmd.Parameters.AddWithValue("$pid", e.Pid); cmd.Parameters.AddWithValue("$pname", e.ProcessName);
        cmd.Parameters.AddWithValue("$proto", e.Protocol.ToString()); cmd.Parameters.AddWithValue("$la", e.LocalAddress);
        cmd.Parameters.AddWithValue("$lp", e.LocalPort); cmd.Parameters.AddWithValue("$ra", (object?)e.RemoteAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rp", e.RemotePort); cmd.Parameters.AddWithValue("$state", e.State.ToString());
        cmd.ExecuteNonQuery();
    }
}

public sealed class AlertRuleRepository
{
    private readonly PortVisualizerDatabase _db;
    public AlertRuleRepository(PortVisualizerDatabase db) => _db = db;

    public List<AlertRule> GetAll()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, RuleName, RuleType, RuleValue, IsEnabled, Severity FROM AlertRules ORDER BY Id";
        var list = new List<AlertRule>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!Enum.TryParse<AlertRuleType>(r.GetString(2), out var type)) continue;
            Enum.TryParse<AlertSeverity>(r.GetString(5), out var sev);
            list.Add(new AlertRule(r.GetInt64(0), r.GetString(1), type, r.GetString(3), r.GetInt32(4) != 0, sev));
        }
        return list;
    }

    public long Save(AlertRule rule)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        if (rule.Id > 0)
        {
            cmd.CommandText = "UPDATE AlertRules SET RuleName=$n, RuleType=$t, RuleValue=$v, IsEnabled=$e, Severity=$s WHERE Id=$id";
            cmd.Parameters.AddWithValue("$id", rule.Id);
        }
        else cmd.CommandText = "INSERT INTO AlertRules (RuleName, RuleType, RuleValue, IsEnabled, Severity) VALUES ($n,$t,$v,$e,$s); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", rule.Name); cmd.Parameters.AddWithValue("$t", rule.Type.ToString());
        cmd.Parameters.AddWithValue("$v", rule.Value); cmd.Parameters.AddWithValue("$e", rule.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", rule.Severity.ToString());
        var res = cmd.ExecuteScalar();
        return rule.Id > 0 ? rule.Id : Convert.ToInt64(res);
    }

    public void Delete(long id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM AlertRules WHERE Id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SeedDefaultsIfEmpty()
    {
        using (var c = _db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM AlertRules";
            if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) return;
        }
        foreach (var r in AlertDefaults.Rules) Save(r);
    }
}

public sealed class SqliteSettingsStore : ISettingsStore
{
    private readonly PortVisualizerDatabase _db;
    public SqliteSettingsStore(PortVisualizerDatabase db) => _db = db;

    public IReadOnlyDictionary<string, string> LoadAll()
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Key, Value FROM Settings";
        var d = new Dictionary<string, string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) d[r.GetString(0)] = r.GetString(1);
        return d;
    }

    public void Save(IEnumerable<KeyValuePair<string, string>> values)
    {
        using var c = _db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO Settings (Key, Value) VALUES ($k,$v) ON CONFLICT(Key) DO UPDATE SET Value=$v";
        var k = cmd.Parameters.Add("$k", SqliteType.Text);
        var v = cmd.Parameters.Add("$v", SqliteType.Text);
        foreach (var kv in values) { k.Value = kv.Key; v.Value = kv.Value; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }
}

/// <summary>
/// Batches database writes on a background task so the polling loop and UI never block on disk I/O.
/// </summary>
public sealed class HistoryWriter : IAsyncDisposable
{
    private sealed record Item(IReadOnlyList<TrackedChange>? Changes, TaskCompletionSource? Flush);

    private readonly HistoryRepository _repo;
    private readonly Channel<Item> _channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;
    private readonly Action<Exception>? _onError;

    public HistoryWriter(HistoryRepository repo, Func<bool>? recordHistory = null, Func<bool>? recordPaths = null, Action<Exception>? onError = null)
    {
        _repo = repo; _onError = onError;
        RecordHistory = recordHistory ?? (() => true);
        RecordPaths = recordPaths ?? (() => true);
        _loop = Task.Run(RunAsync);
    }

    public Func<bool> RecordHistory { get; set; }
    public Func<bool> RecordPaths { get; set; }

    public void Enqueue(IReadOnlyList<TrackedChange> changes)
    {
        if (changes.Count > 0) _channel.Writer.TryWrite(new Item(changes, null));
    }

    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new Item(null, tcs))) tcs.SetResult();
        return tcs.Task;
    }

    private async Task RunAsync()
    {
        var pending = new List<TrackedChange>();
        var flushes = new List<TaskCompletionSource>();
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Gather a batch: everything available now, then a short coalescing window.
            var deadline = DateTime.UtcNow.AddMilliseconds(250);
            while (true)
            {
                while (reader.TryRead(out var item))
                {
                    if (item.Changes is not null) pending.AddRange(item.Changes);
                    if (item.Flush is not null) flushes.Add(item.Flush);
                }
                if (flushes.Count > 0 || pending.Count >= 500 || DateTime.UtcNow >= deadline) break;
                await Task.Delay(50).ConfigureAwait(false);
            }
            try { if (pending.Count > 0) _repo.WriteBatch(pending, RecordHistory(), RecordPaths()); }
            catch (Exception ex) { _onError?.Invoke(ex); }
            pending.Clear();
            foreach (var f in flushes) f.TrySetResult();
            flushes.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }
}
