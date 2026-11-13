using System.Net;
using Microsoft.Extensions.Logging;
using PortVisualizer.Core.Models;
using PortVisualizer.Core.Native;
using PortVisualizer.Core.Processes;

namespace PortVisualizer.Core.Monitoring;

public sealed record TrackedChange(ConnectionEvent Event, TrackedConnection Connection);

public sealed record TrackerResult(
    IReadOnlyList<TrackedChange> Changes,
    IReadOnlyList<TrackedConnection> Active,
    IReadOnlyList<TrackedConnection> RecentlyClosed,
    bool IsBaseline)
{
    public IEnumerable<ConnectionEvent> Events => Changes.Select(c => c.Event);
}

/// <summary>
/// Compares successive snapshots. The first snapshot is a baseline: it populates state but emits no events,
/// so existing connections are not reported as "new". Stable connections never re-emit events.
/// </summary>
public sealed class ConnectionTracker
{
    private readonly Dictionary<string, TrackedConnection> _active = new();
    private readonly List<TrackedConnection> _closed = new();
    private readonly HashSet<string> _knownRemoteIps = new();
    private readonly HashSet<string> _knownProcesses = new();
    private bool _baselineDone;

    public TimeSpan ClosedRetention { get; set; } = TimeSpan.FromSeconds(10);
    public IReadOnlyCollection<string> KnownRemoteIps => _knownRemoteIps;

    public void Reset()
    {
        _active.Clear(); _closed.Clear(); _knownRemoteIps.Clear(); _knownProcesses.Clear(); _baselineDone = false;
    }

    public TrackerResult Update(DateTime nowUtc, IReadOnlyList<Connection> current)
    {
        var changes = new List<TrackedChange>();
        var baseline = !_baselineDone;

        var currentByKey = new Dictionary<string, Connection>(current.Count);
        foreach (var c in current)
        {
            // Closed sockets in TIME_WAIT are owned by PID 0: keep the process we already know.
            if (c.Record.Pid == 0 && c.Protocol == Protocol.Tcp && _active.TryGetValue(c.Key, out var prev) && prev.Connection.Record.Pid != 0)
                currentByKey[c.Key] = c with { Process = prev.Connection.Process };
            else
                currentByKey[c.Key] = c;
        }

        // Updates and removals.
        foreach (var (key, tracked) in _active.ToList())
        {
            if (currentByKey.TryGetValue(key, out var cur))
            {
                var old = tracked.Connection;
                // Same 4-tuple but a different live process => the PID was reused; treat as close + new.
                if (cur.Record.Pid != 0 && old.Record.Pid != 0 && cur.Process.Key != old.Process.Key)
                {
                    CloseTracked(nowUtc, key, tracked, changes);
                    continue;
                }
                tracked.LastObservedUtc = nowUtc;
                if (old.State != cur.State)
                {
                    tracked.Connection = cur;
                    changes.Add(new(MakeEvent(nowUtc, ChangeType.StateChanged, cur, old.State), tracked));
                }
                else if (!ReferenceEquals(old.Process, cur.Process)) tracked.Connection = cur;
            }
            else
            {
                CloseTracked(nowUtc, key, tracked, changes);
            }
        }

        // Additions.
        foreach (var (key, cur) in currentByKey)
        {
            if (_active.ContainsKey(key)) continue;
            var tracked = new TrackedConnection(cur, nowUtc) { DetectedAfterBaseline = !baseline };
            _active[key] = tracked;

            var type = cur.IsListening ? ChangeType.NewListeningPort : cur.IsUdp ? ChangeType.NewUdpEndpoint : ChangeType.NewConnection;
            var isNewProcess = _knownProcesses.Add(cur.Process.Key) && cur.Record.Pid != 0;
            var isNewRemote = cur.HasRemote && _knownRemoteIps.Add(cur.RemoteAddress!);
            if (baseline) continue;

            changes.Add(new(MakeEvent(nowUtc, type, cur, null), tracked));
            if (isNewRemote) changes.Add(new(MakeEvent(nowUtc, ChangeType.NewRemoteIp, cur, null), tracked));
            if (isNewProcess) changes.Add(new(MakeEvent(nowUtc, ChangeType.NewProcess, cur, null), tracked));
        }

        _closed.RemoveAll(c => nowUtc - c.ClosedUtc!.Value > ClosedRetention);
        _baselineDone = true;
        return new TrackerResult(changes, _active.Values.ToList(), _closed.ToList(), baseline);
    }

    private void CloseTracked(DateTime now, string key, TrackedConnection t, List<TrackedChange> changes)
    {
        _active.Remove(key);
        t.ClosedUtc = now;
        _closed.Add(t);
        var c = t.Connection;
        var type = c.IsListening ? ChangeType.ListeningPortClosed : c.IsUdp ? ChangeType.UdpEndpointRemoved : ChangeType.ConnectionClosed;
        changes.Add(new(MakeEvent(now, type, c, null), t));
    }

    private static ConnectionEvent MakeEvent(DateTime now, ChangeType type, Connection c, TcpState? prev) => new(
        now, type, c.Record.Pid, c.Process.Name, c.Process.Path, c.Process.Key, c.Process.StartTimeUtc,
        c.Protocol, c.LocalAddress, c.LocalPort, c.RemoteAddress, c.RemotePort, c.State, prev, c.Key);
}

public enum MonitorState { Stopped, Running, Paused }

public sealed record MonitorCycleResult(DateTime TimestampUtc, TrackerResult Tracker, IReadOnlyList<string> Warnings, TimeSpan Duration);

/// <summary>
/// Polls the connection tables on a configurable interval. Cycles never overlap: the loop awaits each poll
/// before sleeping, and PollOnceAsync is additionally guarded by a semaphore.
/// </summary>
public sealed class NetworkMonitor : IAsyncDisposable
{
    private readonly IConnectionSource _source;
    private readonly IProcessResolver _resolver;
    private readonly ConnectionTracker _tracker;
    private readonly Func<DateTime> _clock;
    private readonly ILogger? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile MonitorOptions _options = MonitorOptions.Default;
    private long _intervalMs = 1000;

    public NetworkMonitor(IConnectionSource source, IProcessResolver resolver, ConnectionTracker tracker,
        ILogger? log = null, Func<DateTime>? clock = null)
    {
        _source = source; _resolver = resolver; _tracker = tracker; _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public ConnectionTracker Tracker => _tracker;
    public MonitorState State { get; private set; } = MonitorState.Stopped;
    public DateTime? LastRefreshUtc { get; private set; }
    public string? LastError { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public int SkippedOverlaps { get; private set; }

    public event Action<MonitorCycleResult>? CycleCompleted;
    public event Action<string>? ErrorOccurred;
    public event Action<MonitorState>? StateChanged;

    public MonitorOptions Options { get => _options; set => _options = value; }

    public TimeSpan Interval
    {
        get => TimeSpan.FromMilliseconds(Interlocked.Read(ref _intervalMs));
        set => Interlocked.Exchange(ref _intervalMs, (long)Math.Clamp(value.TotalMilliseconds, 250, 3_600_000));
    }

    public void Start()
    {
        if (_loop is { IsCompleted: false }) { Resume(); return; }
        _cts = new CancellationTokenSource();
        SetState(MonitorState.Running);
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Pause() { if (State == MonitorState.Running) SetState(MonitorState.Paused); }
    public void Resume() { if (State == MonitorState.Paused) SetState(MonitorState.Running); }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;
        cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        cts.Dispose();
        _cts = null;
        SetState(MonitorState.Stopped);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (State == MonitorState.Running)
            {
                try { await PollOnceAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            try { await Task.Delay(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Runs one cycle. Returns null if a cycle was already running, cancelled, or the read failed.</summary>
    public async Task<MonitorCycleResult?> PollOnceAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) { SkippedOverlaps++; return null; }
        try
        {
            ct.ThrowIfCancellationRequested();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ConnectionReadResult read;
            try
            {
                read = await Task.Run(() => _source.Read(_options), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                ConsecutiveFailures++;
                LastError = ex.Message;
                _log?.LogWarning(ex, "Connection table read failed");
                ErrorOccurred?.Invoke(ex.Message);
                return null;
            }
            ct.ThrowIfCancellationRequested();

            var now = _clock();
            var resolved = new List<Connection>(read.Records.Count);
            var pids = new HashSet<int>();
            foreach (var r in read.Records)
            {
                pids.Add(r.Pid);
                ProcessInfo p;
                try { p = _resolver.Resolve(r.Pid); }
                catch (Exception ex)
                {
                    _log?.LogDebug(ex, "Process resolution failed for {Pid}", r.Pid);
                    p = ProcessInfo.Missing(r.Pid);
                }
                resolved.Add(new Connection(r, p));
            }
            _resolver.Prune(pids);

            var tracked = _tracker.Update(now, resolved);
            ConsecutiveFailures = 0;
            LastError = read.Warnings.Count > 0 ? string.Join("; ", read.Warnings) : null;
            LastRefreshUtc = now;
            var result = new MonitorCycleResult(now, tracked, read.Warnings, sw.Elapsed);
            CycleCompleted?.Invoke(result);
            return result;
        }
        finally { _gate.Release(); }
    }

    private void SetState(MonitorState s) { State = s; StateChanged?.Invoke(s); }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
