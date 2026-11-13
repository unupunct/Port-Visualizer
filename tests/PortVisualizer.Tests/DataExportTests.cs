using System.Text.Json;
using PortVisualizer.Core.Alerts;
using PortVisualizer.Core.Export;
using PortVisualizer.Core.Models;
using PortVisualizer.Core.Monitoring;
using PortVisualizer.Core.Settings;
using PortVisualizer.Data;

namespace PortVisualizer.Tests;

public sealed class TempDb : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pv-tests-" + Guid.NewGuid().ToString("N"));
    public PortVisualizerDatabase Db { get; }

    public TempDb()
    {
        Directory.CreateDirectory(Dir);
        Db = new PortVisualizerDatabase(Path.Combine(Dir, "test.db"));
        Db.Migrate();
    }

    public void Dispose()
    {
        PortVisualizerDatabase.ReleaseFiles();
        try { Directory.Delete(Dir, true); } catch { }
    }
}

public class DatabaseTests
{
    private static List<TrackedChange> Changes(ConnectionTracker t, DateTime now, params Connection[] conns) =>
        t.Update(now, conns).Changes.ToList();

    [Fact]
    public void Migrations_are_idempotent_and_create_all_tables()
    {
        using var tmp = new TempDb();
        tmp.Db.Migrate(); tmp.Db.Migrate();
        Assert.Equal(PortVisualizerDatabase.LatestVersion, tmp.Db.GetVersion());
        using var c = tmp.Db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        var tables = new List<string>();
        using (var r = cmd.ExecuteReader()) while (r.Read()) tables.Add(r.GetString(0));
        foreach (var t in new[] { "Processes", "ConnectionEvents", "CurrentConnections", "AlertRules", "Settings" }) Assert.Contains(t, tables);
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'IX_%'";
        Assert.True(Convert.ToInt32(cmd.ExecuteScalar()) >= 8);
    }

    [Fact]
    public void Events_are_persisted_and_queryable_with_all_filters()
    {
        using var tmp = new TempDb();
        var repo = new HistoryRepository(tmp.Db);
        var t = new ConnectionTracker();
        t.Update(C.T0, Array.Empty<Connection>());
        var chrome = C.Proc(100, "chrome.exe"); var svc = C.Proc(200, "svchost.exe");
        repo.WriteBatch(Changes(t, C.T0.AddMinutes(1),
            C.Tcp("10.0.0.2", 5000, "142.250.1.1", 443, TcpState.Established, chrome),
            C.Tcp("10.0.0.2", 5001, "20.1.1.1", 8080, TcpState.Established, svc),
            C.Listen(135, svc)));
        repo.WriteBatch(Changes(t, C.T0.AddMinutes(10), C.Tcp("10.0.0.2", 5001, "20.1.1.1", 8080, TcpState.Established, svc), C.Listen(135, svc)));

        Assert.Equal(8, repo.Count(new HistoryQuery()));
        Assert.All(repo.Query(new HistoryQuery { Process = "chrome" }), e => Assert.Equal("chrome.exe", e.ProcessName));
        Assert.Equal(4, repo.Query(new HistoryQuery { Process = "chrome" }).Count);
        Assert.Equal(1, repo.Count(new HistoryQuery { Ip = "142.250", Types = { ChangeType.NewConnection } }));
        Assert.Equal(1, repo.Query(new HistoryQuery { Port = 443, Types = { ChangeType.ConnectionClosed } }).Count);
        Assert.Equal(1, repo.Count(new HistoryQuery { Port = 135, Types = { ChangeType.NewListeningPort } }));
        Assert.Equal(1, repo.Count(new HistoryQuery { FromUtc = C.T0.AddMinutes(5) , Types = { ChangeType.ConnectionClosed } }));
        Assert.Equal(0, repo.Count(new HistoryQuery { ToUtc = C.T0, }));
        var first = repo.Query(new HistoryQuery { Limit = 2 });
        Assert.Equal(2, first.Count);
        Assert.True(first[0].TimestampUtc >= first[1].TimestampUtc);
        Assert.Equal(C.T0.AddMinutes(1), repo.Query(new HistoryQuery { Types = { ChangeType.NewListeningPort } })[0].TimestampUtc);
    }

    [Fact]
    public void Like_wildcards_in_search_text_are_escaped()
    {
        using var tmp = new TempDb();
        var repo = new HistoryRepository(tmp.Db);
        var t = new ConnectionTracker(); t.Update(C.T0, Array.Empty<Connection>());
        repo.WriteBatch(Changes(t, C.T0.AddSeconds(1), C.Tcp("10.0.0.2", 1, "1.2.3.4", 1, TcpState.Established, C.Proc(1, "a_b.exe"))));
        Assert.Equal(0, repo.Count(new HistoryQuery { Process = "%" }));
        Assert.True(repo.Count(new HistoryQuery { Process = "a_b" }) > 0);
    }

    [Fact]
    public void Current_connections_hold_only_live_state_without_snapshots_each_cycle()
    {
        using var tmp = new TempDb();
        var repo = new HistoryRepository(tmp.Db);
        var t = new ConnectionTracker(); t.Update(C.T0, Array.Empty<Connection>());
        var c = C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, C.Proc(1));
        repo.WriteBatch(Changes(t, C.T0.AddSeconds(1), c));
        Assert.Equal(1, repo.CountCurrentConnections());
        for (var i = 2; i < 30; i++) repo.WriteBatch(Changes(t, C.T0.AddSeconds(i), c)); // stable => no events, no writes
        var eventsBefore = repo.Count(new HistoryQuery());
        Assert.Equal(3, eventsBefore); // NewConnection, NewRemoteIp, NewProcess
        repo.WriteBatch(Changes(t, C.T0.AddSeconds(40)));
        Assert.Equal(0, repo.CountCurrentConnections());
    }

    [Fact]
    public void Process_identity_distinguishes_reused_pids()
    {
        using var tmp = new TempDb();
        var repo = new HistoryRepository(tmp.Db);
        var t = new ConnectionTracker(); t.Update(C.T0, Array.Empty<Connection>());
        repo.WriteBatch(Changes(t, C.T0.AddSeconds(1), C.Tcp("10.0.0.2", 1, "1.1.1.1", 1, TcpState.Established, C.Proc(10, "old.exe", 0))));
        repo.WriteBatch(Changes(t, C.T0.AddSeconds(2), C.Tcp("10.0.0.2", 2, "1.1.1.2", 1, TcpState.Established, C.Proc(10, "new.exe", 900))));
        Assert.Equal(2, repo.CountProcesses());
    }

    [Fact]
    public void Recording_can_be_disabled_and_paths_suppressed()
    {
        using var tmp = new TempDb();
        var repo = new HistoryRepository(tmp.Db);
        var t = new ConnectionTracker(); t.Update(C.T0, Array.Empty<Connection>());
        repo.WriteBatch(Changes(t, C.T0.AddSeconds(1), C.Tcp("10.0.0.2", 1, "1.1.1.1", 1, TcpState.Established, C.Proc(1))), recordHistory: false);
        Assert.Equal(0, repo.Count(new HistoryQuery()));
        Assert.Equal(1, repo.CountCurrentConnections());

        repo.WriteBatch(Changes(t, C.T0.AddSeconds(2), C.Tcp("10.0.0.2", 2, "1.1.1.2", 1, TcpState.Established, C.Proc(2, "x.exe"))), recordPaths: false);
        Assert.All(repo.Query(new HistoryQuery { Process = "x.exe" }), e => Assert.Null(e.ProcessPath));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 2)]
    [InlineData(30, 3)]
    [InlineData(90, 4)]
    [InlineData(0, 5)]
    public void Retention_removes_only_events_older_than_the_window(int days, int expectedRemaining)
    {
        using var tmp = new TempDb();
        var repo = new HistoryRepository(tmp.Db);
        var now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        foreach (var age in new[] { 0.5, 5, 20, 60, 400 })
            repo.InsertRawEventForTest(new ConnectionEvent(now.AddDays(-age), ChangeType.NewConnection, 1, "a.exe", null, "1:0", null,
                Protocol.Tcp, "10.0.0.2", 1, "1.1.1.1", 2, TcpState.Established, null, "k"));
        repo.ApplyRetention(days, now);
        Assert.Equal(expectedRemaining, repo.Count(new HistoryQuery()));
    }

    [Fact]
    public void Clear_history_and_backup_work()
    {
        using var tmp = new TempDb();
        var repo = new HistoryRepository(tmp.Db);
        var t = new ConnectionTracker(); t.Update(C.T0, Array.Empty<Connection>());
        repo.WriteBatch(Changes(t, C.T0.AddSeconds(1), C.Tcp("10.0.0.2", 1, "1.1.1.1", 1, TcpState.Established, C.Proc(1))));
        var backup = Path.Combine(tmp.Dir, "backup.db");
        tmp.Db.Backup(backup);
        tmp.Db.Backup(backup); // overwriting an existing backup must work
        Assert.True(new FileInfo(backup).Length > 0);
        var copy = new PortVisualizerDatabase(backup);
        Assert.True(new HistoryRepository(copy).Count(new HistoryQuery()) > 0);
        repo.ClearHistory();
        Assert.Equal(0, repo.Count(new HistoryQuery()));
        PortVisualizerDatabase.ReleaseFiles();
    }

    [Fact]
    public void Timeline_buckets_events()
    {
        using var tmp = new TempDb();
        var repo = new HistoryRepository(tmp.Db);
        var t = new ConnectionTracker(); t.Update(C.T0, Array.Empty<Connection>());
        var c = C.Tcp("10.0.0.2", 1, "1.1.1.1", 1, TcpState.Established, C.Proc(1));
        repo.WriteBatch(Changes(t, C.T0.AddMinutes(1), c));
        repo.WriteBatch(Changes(t, C.T0.AddMinutes(20)));
        var tl = repo.Timeline(C.T0, C.T0.AddHours(1), 10);
        Assert.Equal(2, tl.Count);
        Assert.Equal(1, tl[0].Opened);
        Assert.Equal(1, tl[1].Closed);
    }

    [Fact]
    public void Alert_rules_and_settings_persist()
    {
        using var tmp = new TempDb();
        var rules = new AlertRuleRepository(tmp.Db);
        rules.SeedDefaultsIfEmpty(); rules.SeedDefaultsIfEmpty();
        Assert.Equal(AlertDefaults.Rules.Count, rules.GetAll().Count);
        var id = rules.Save(new AlertRule(0, "block", AlertRuleType.BlockedIp, "1.2.3.4", true, AlertSeverity.Critical));
        var saved = rules.GetAll().Single(r => r.Id == id);
        Assert.Equal(AlertSeverity.Critical, saved.Severity);
        rules.Save(saved with { IsEnabled = false });
        Assert.False(rules.GetAll().Single(r => r.Id == id).IsEnabled);
        rules.Delete(id);
        Assert.DoesNotContain(rules.GetAll(), r => r.Id == id);

        var store = new SqliteSettingsStore(tmp.Db);
        store.Save(new AppSettings { RetentionDays = 90, DarkTheme = false }.ToDictionary());
        var loaded = AppSettings.From(store.LoadAll());
        Assert.Equal(90, loaded.RetentionDays);
        Assert.False(loaded.DarkTheme);
    }

    [Fact]
    public async Task Writer_batches_in_background_and_flushes_on_dispose()
    {
        using var tmp = new TempDb();
        var repo = new HistoryRepository(tmp.Db);
        var t = new ConnectionTracker(); t.Update(C.T0, Array.Empty<Connection>());
        var writer = new HistoryWriter(repo);
        for (var i = 0; i < 20; i++)
            writer.Enqueue(Changes(t, C.T0.AddSeconds(i + 1), C.Tcp("10.0.0.2", 1000 + i, $"1.1.1.{i + 1}", 443, TcpState.Established, C.Proc(1))));
        await writer.FlushAsync();
        Assert.True(repo.Count(new HistoryQuery()) >= 20);
        writer.Enqueue(Changes(t, C.T0.AddSeconds(100)));
        await writer.DisposeAsync();
        Assert.True(repo.Count(new HistoryQuery { Types = { ChangeType.ConnectionClosed } }) > 0);
    }

    [Fact]
    public void Large_batches_persist_in_one_transaction()
    {
        using var tmp = new TempDb();
        var repo = new HistoryRepository(tmp.Db);
        var t = new ConnectionTracker(); t.Update(C.T0, Array.Empty<Connection>());
        var p = C.Proc(1);
        var conns = Enumerable.Range(0, 5000).Select(i => C.Tcp("10.0.0.2", 1000 + i, $"30.0.{i / 250}.{i % 250 + 1}", 443, TcpState.Established, p)).ToArray();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        repo.WriteBatch(Changes(t, C.T0.AddSeconds(1), conns));
        Assert.True(sw.ElapsedMilliseconds < 8000, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(5000, repo.CountCurrentConnections());
    }
}

public class ExportTests
{
    private static List<TrackedConnection> Sample() => new()
    {
        C.Tracked(C.Tcp("192.168.1.10", 51234, "142.250.1.1", 443, TcpState.Established, C.Proc(100, "chrome.exe"))),
        C.Tracked(C.Udp(5353, C.Proc(300, "=cmd|' /C calc'!A0.exe"))),
        C.Tracked(C.Tcp("fe80::1", 6000, "2606:4700::1", 443, TcpState.Established, C.Proc(400, "disc,\"ord\".exe"))),
    };

    private static readonly DateTime Stamp = new(2026, 5, 2, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Csv_has_header_metadata_rows_and_escapes_special_values()
    {
        var csv = Exporter.ToCsv(ExportBuilder.Connections("Connections", Sample(), Stamp));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("exported 2026-05-02T08:00:00.000Z", lines[0]);
        Assert.StartsWith("Process,PID,ExecutablePath,Protocol,LocalAddress,LocalPort,RemoteAddress,RemotePort,State,FirstObservedUtc,LastObservedUtc", lines[1]);
        Assert.Equal(5, lines.Length);
        Assert.Contains("chrome.exe,100,", lines[2]);
        Assert.Contains("ESTABLISHED", lines[2]);
        Assert.StartsWith("'=cmd", lines[3]);                       // formula injection neutralised
        Assert.Contains("\"disc,\"\"ord\"\".exe\"", lines[4]);      // quoting
    }

    [Fact]
    public void Json_is_valid_and_contains_required_fields()
    {
        var json = Exporter.ToJson(ExportBuilder.Connections("Connections", Sample(), Stamp));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("2026-05-02T08:00:00.000Z", root.GetProperty("exportedUtc").GetString());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        var row = root.GetProperty("rows")[0];
        Assert.Equal("chrome.exe", row.GetProperty("Process").GetString());
        Assert.Equal("142.250.1.1", row.GetProperty("RemoteAddress").GetString());
        Assert.Equal("TCP", row.GetProperty("Protocol").GetString());
        Assert.Equal("ESTABLISHED", row.GetProperty("State").GetString());
        Assert.False(string.IsNullOrEmpty(row.GetProperty("FirstObservedUtc").GetString()));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("rows")[1].GetProperty("RemoteAddress").ValueKind); // UDP: no invented remote
    }

    [Fact]
    public void Html_is_escaped_and_includes_range_for_history()
    {
        var ev = new ConnectionEvent(C.T0, ChangeType.NewConnection, 1, "<script>alert(1)</script>.exe", @"C:\x.exe", "1:0", null, Protocol.Tcp,
            "10.0.0.2", 1, "1.1.1.1", 2, TcpState.Established, null, "k");
        var html = Exporter.ToHtml(ExportBuilder.History(new[] { ev }, C.T0.AddDays(-1), C.T0, Stamp));
        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Date range", html);
        Assert.Contains("2026-04-30T10:00:00.000Z", html);
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<th>EventType</th>", html);
    }

    [Fact]
    public void Process_summary_counts_by_protocol()
    {
        var p = C.Proc(1, "a.exe");
        var items = new[]
        {
            C.Tracked(C.Tcp("10.0.0.2", 1, "1.1.1.1", 443, TcpState.Established, p)),
            C.Tracked(C.Tcp("10.0.0.2", 2, "1.1.1.1", 80, TcpState.Established, p)),
            C.Tracked(C.Udp(53, p)), C.Tracked(C.Listen(80, p)),
        };
        var table = ExportBuilder.ProcessSummary(items, Stamp);
        var row = Assert.Single(table.Rows);
        var cols = table.Columns.ToList();
        Assert.Equal("4", row[cols.IndexOf("Connections")]);
        Assert.Equal("2", row[cols.IndexOf("TcpConnections")]);
        Assert.Equal("1", row[cols.IndexOf("UdpEndpoints")]);
        Assert.Equal("1", row[cols.IndexOf("ListeningPorts")]);
        Assert.Equal("1", row[cols.IndexOf("RemoteIps")]);
    }

    [Fact]
    public async Task Files_are_written_in_every_format()
    {
        using var tmp = new TempDb();
        foreach (var f in Enum.GetValues<ExportFormat>())
        {
            var path = Path.Combine(tmp.Dir, "out" + Exporter.Extension(f));
            await Exporter.WriteAsync(path, ExportBuilder.Connections("t", Sample()), f);
            Assert.True(new FileInfo(path).Length > 100);
        }
    }
}
