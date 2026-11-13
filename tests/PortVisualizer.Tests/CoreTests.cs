using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PortVisualizer.Core.Alerts;
using PortVisualizer.Core.Filtering;
using PortVisualizer.Core.Graph;
using PortVisualizer.Core.Models;
using PortVisualizer.Core.Monitoring;
using PortVisualizer.Core.Native;
using PortVisualizer.Core.Processes;
using PortVisualizer.Core.Settings;

namespace PortVisualizer.Tests;

public class ParsingTests
{
    [Fact]
    public void Tcp4_rows_are_parsed_with_ports_state_and_pid()
    {
        var buf = TableBuilder.Tcp4(("192.168.1.10", 51234, "142.250.80.46", 443, TcpState.Established, 4321),
                                    ("0.0.0.0", 135, "0.0.0.0", 0, TcpState.Listen, 1000));
        var rows = TableParser.ParseTcp4(buf);
        Assert.Equal(2, rows.Count);
        Assert.Equal(IPAddress.Parse("192.168.1.10"), rows[0].LocalAddress);
        Assert.Equal(51234, rows[0].LocalPort);
        Assert.Equal(IPAddress.Parse("142.250.80.46"), rows[0].RemoteAddress);
        Assert.Equal(443, rows[0].RemotePort);
        Assert.Equal(TcpState.Established, rows[0].State);
        Assert.Equal(4321, rows[0].Pid);
        Assert.Equal(TcpState.Listen, rows[1].State);
        Assert.Equal(135, rows[1].LocalPort);
    }

    [Fact]
    public void Udp4_rows_have_no_remote_endpoint_or_state()
    {
        var rows = TableParser.ParseUdp4(TableBuilder.Udp4(("0.0.0.0", 5353, 77), ("127.0.0.1", 1900, 88)));
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => { Assert.Null(r.RemoteAddress); Assert.Equal(0, r.RemotePort); Assert.Equal(TcpState.None, r.State); Assert.Equal(Protocol.Udp, r.Protocol); });
        Assert.Equal(5353, rows[0].LocalPort);
        Assert.Equal(88, rows[1].Pid);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    public void IPv4_addresses_round_trip(string ip)
    {
        var r = TableParser.ParseTcp4(TableBuilder.Tcp4((ip, 80, ip, 81, TcpState.Established, 1)))[0];
        Assert.Equal(IPAddress.Parse(ip), r.LocalAddress);
        Assert.False(r.IsIPv6);
    }

    [Fact]
    public void IPv6_tcp_and_udp_rows_are_parsed()
    {
        var tcp = TableParser.ParseTcp6(TableBuilder.Tcp6(("fe80::1", 3, 50000, "2606:4700::6810:85e5", 443, TcpState.Established, 12)));
        Assert.Single(tcp);
        Assert.True(tcp[0].IsIPv6);
        Assert.Equal(IPAddress.Parse("2606:4700::6810:85e5"), tcp[0].RemoteAddress);
        Assert.Equal(3, tcp[0].LocalAddress.ScopeId);
        Assert.Equal(443, tcp[0].RemotePort);
        Assert.Equal(12, tcp[0].Pid);

        var udp = TableParser.ParseUdp6(TableBuilder.Udp6(("::", 546, 900), ("::1", 5353, 901)));
        Assert.Equal(2, udp.Count);
        Assert.Equal(IPAddress.IPv6Loopback, udp[1].LocalAddress);
        Assert.Equal(546, udp[0].LocalPort);
    }

    [Fact]
    public void Truncated_or_empty_buffers_do_not_throw()
    {
        Assert.Empty(TableParser.ParseTcp4(Array.Empty<byte>()));
        var full = TableBuilder.Tcp4(("1.1.1.1", 1, "2.2.2.2", 2, TcpState.Established, 1), ("1.1.1.1", 3, "2.2.2.2", 4, TcpState.Established, 1));
        Assert.Single(TableParser.ParseTcp4(full.AsSpan(0, full.Length - 10)));
    }

    [Fact]
    public void Real_windows_source_sees_our_own_listening_socket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var result = new WindowsConnectionSource().Read(MonitorOptions.Default);
        Assert.Contains(result.Records, r => r.Protocol == Protocol.Tcp && r.State == TcpState.Listen && r.LocalPort == port && r.Pid == Environment.ProcessId);
        Assert.Contains(result.Records, r => r.Protocol == Protocol.Udp && r.LocalPort == udpPort && r.Pid == Environment.ProcessId);
    }

    [Fact]
    public void Real_windows_source_respects_protocol_options()
    {
        var onlyUdp = new WindowsConnectionSource().Read(new MonitorOptions(Tcp: false));
        Assert.All(onlyUdp.Records, r => Assert.Equal(Protocol.Udp, r.Protocol));
        var noListen = new WindowsConnectionSource().Read(new MonitorOptions(Udp: false, IncludeListening: false));
        Assert.DoesNotContain(noListen.Records, r => r.State == TcpState.Listen);
    }

    [Fact]
    public void Real_process_api_identifies_current_process()
    {
        var api = new WindowsProcessApi();
        var raw = api.Query(Environment.ProcessId);
        Assert.NotNull(raw);
        Assert.False(string.IsNullOrEmpty(raw!.Path));
        Assert.NotNull(raw.StartTimeUtc);
        Assert.Equal(Process.GetCurrentProcess().StartTime.ToUniversalTime(), raw.StartTimeUtc!.Value, TimeSpan.FromSeconds(1));
    }
}

public class ProcessTests
{
    [Fact]
    public void Resolver_returns_name_path_and_caches()
    {
        var api = new FakeProcessApi().Add(10, "chrome.exe");
        var r = new ProcessResolver(api);
        var a = r.Resolve(10); var b = r.Resolve(10);
        Assert.Equal("chrome.exe", a.Name);
        Assert.Equal(@"C:\apps\chrome.exe", a.Path);
        Assert.Same(a, b);
        Assert.Equal(1, api.QueryCalls);
    }

    [Fact]
    public void Missing_process_is_handled_without_throwing()
    {
        var r = new ProcessResolver(new FakeProcessApi());
        var p = r.Resolve(999);
        Assert.Contains("999", p.Name);
        Assert.Null(p.Path);
    }

    [Fact]
    public void Access_denied_is_handled_gracefully()
    {
        var api = new FakeProcessApi { ThrowOnQuery = true };
        api.Processes[5] = new RawProcess("x", null, null, true);
        var r = new ProcessResolver(api);
        var p = r.Resolve(5);
        Assert.True(p.AccessDenied);
        Assert.Null(p.Path);
    }

    [Fact]
    public void Pid_reuse_produces_a_new_process_identity()
    {
        var api = new FakeProcessApi().Add(10, "old.exe", C.T0);
        var r = new ProcessResolver(api);
        var first = r.Resolve(10);
        api.Add(10, "new.exe", C.T0.AddHours(1));
        var second = r.Resolve(10);
        Assert.Equal("old.exe", first.Name);
        Assert.Equal("new.exe", second.Name);
        Assert.NotEqual(first.Key, second.Key);
    }

    [Fact]
    public void Path_recording_can_be_disabled()
    {
        var r = new ProcessResolver(new FakeProcessApi().Add(10, "a.exe")) { RecordPaths = false };
        Assert.Null(r.Resolve(10).Path);
    }

    [Fact]
    public void Prune_drops_stale_cache_entries()
    {
        var api = new FakeProcessApi();
        for (var i = 1; i <= 40; i++) api.Add(i, $"p{i}.exe");
        var r = new ProcessResolver(api);
        for (var i = 1; i <= 40; i++) r.Resolve(i);
        r.Prune(new HashSet<int> { 1 });
        r.Resolve(5);
        Assert.Equal(41, api.QueryCalls);
    }
}

public class TrackerTests
{
    private static ConnectionTracker Baselined(params Connection[] initial)
    {
        var t = new ConnectionTracker();
        t.Update(C.T0, initial);
        return t;
    }

    [Fact]
    public void First_snapshot_is_baseline_without_events()
    {
        var t = new ConnectionTracker();
        var res = t.Update(C.T0, new[] { C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, C.Proc(1)) });
        Assert.True(res.IsBaseline);
        Assert.Empty(res.Changes);
        Assert.Single(res.Active);
    }

    [Fact]
    public void New_connection_is_detected_with_remote_ip_and_process_events()
    {
        var t = Baselined();
        var c = C.Tcp("192.168.1.10", 51234, "142.250.1.1", 443, TcpState.Established, C.Proc(7, "chrome.exe"));
        var res = t.Update(C.T0.AddSeconds(1), new[] { c });
        var types = res.Events.Select(e => e.Type).ToList();
        Assert.Contains(ChangeType.NewConnection, types);
        Assert.Contains(ChangeType.NewRemoteIp, types);
        Assert.Contains(ChangeType.NewProcess, types);
        var ev = res.Events.First(e => e.Type == ChangeType.NewConnection);
        Assert.Equal("chrome.exe", ev.ProcessName);
        Assert.Equal("192.168.1.10:51234 -> 142.250.1.1:443", ev.Endpoints);
    }

    [Fact]
    public void Closed_connection_is_detected_and_kept_briefly()
    {
        var c = C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, C.Proc(1));
        var t = Baselined(c);
        var res = t.Update(C.T0.AddSeconds(1), Array.Empty<Connection>());
        Assert.Contains(res.Events, e => e.Type == ChangeType.ConnectionClosed);
        Assert.Single(res.RecentlyClosed);
        Assert.Empty(res.Active);
        var later = t.Update(C.T0.AddSeconds(30), Array.Empty<Connection>());
        Assert.Empty(later.RecentlyClosed);
    }

    [Fact]
    public void Tcp_state_changes_emit_a_single_event()
    {
        var p = C.Proc(1);
        var t = Baselined(C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, p));
        var res = t.Update(C.T0.AddSeconds(1), new[] { C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.CloseWait, p) });
        var ev = Assert.Single(res.Events);
        Assert.Equal(ChangeType.StateChanged, ev.Type);
        Assert.Equal(TcpState.Established, ev.PreviousState);
        Assert.Equal(TcpState.CloseWait, ev.State);
    }

    [Fact]
    public void Time_wait_with_pid_zero_keeps_the_original_process()
    {
        var p = C.Proc(33, "svc.exe");
        var t = Baselined(C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, p));
        var tw = C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.TimeWait, C.Proc(0, "System Idle"));
        var res = t.Update(C.T0.AddSeconds(1), new[] { tw });
        var ev = Assert.Single(res.Events);
        Assert.Equal(ChangeType.StateChanged, ev.Type);
        Assert.Equal("svc.exe", ev.ProcessName);
        Assert.Equal(33, res.Active[0].Connection.Process.Pid);
    }

    [Fact]
    public void Udp_endpoints_added_and_removed()
    {
        var p = C.Proc(2);
        var t = Baselined();
        var added = t.Update(C.T0.AddSeconds(1), new[] { C.Udp(5353, p) });
        Assert.Contains(added.Events, e => e.Type == ChangeType.NewUdpEndpoint && e.RemoteAddress is null);
        var removed = t.Update(C.T0.AddSeconds(2), Array.Empty<Connection>());
        Assert.Contains(removed.Events, e => e.Type == ChangeType.UdpEndpointRemoved);
    }

    [Fact]
    public void New_listening_port_is_reported()
    {
        var t = Baselined();
        var res = t.Update(C.T0.AddSeconds(1), new[] { C.Listen(8080, C.Proc(9, "node.exe")) });
        Assert.Contains(res.Events, e => e.Type == ChangeType.NewListeningPort && e.LocalPort == 8080);
        Assert.DoesNotContain(res.Events, e => e.Type == ChangeType.NewRemoteIp);
    }

    [Fact]
    public void Stable_connections_do_not_generate_duplicate_events()
    {
        var c = C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, C.Proc(1));
        var t = Baselined();
        var first = t.Update(C.T0.AddSeconds(1), new[] { c });
        Assert.NotEmpty(first.Events);
        for (var i = 2; i < 12; i++)
            Assert.Empty(t.Update(C.T0.AddSeconds(i), new[] { c }).Events);
    }

    [Fact]
    public void Remote_ip_is_new_only_once_across_connections_and_processes()
    {
        var t = Baselined();
        t.Update(C.T0.AddSeconds(1), new[] { C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, C.Proc(1)) });
        var res = t.Update(C.T0.AddSeconds(2), new[]
        {
            C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, C.Proc(1)),
            C.Tcp("10.0.0.2", 5001, "8.8.8.8", 80, TcpState.Established, C.Proc(2)),
        });
        Assert.DoesNotContain(res.Events, e => e.Type == ChangeType.NewRemoteIp);
        Assert.Contains(res.Events, e => e.Type == ChangeType.NewProcess);
    }

    [Fact]
    public void Pid_reuse_on_same_tuple_is_closed_then_new()
    {
        var old = C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, C.Proc(10, "old.exe", startSec: 0));
        var t = Baselined(old);
        var reused = C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, C.Proc(10, "new.exe", startSec: 500));
        var res = t.Update(C.T0.AddSeconds(1), new[] { reused });
        Assert.Contains(res.Events, e => e.Type == ChangeType.ConnectionClosed && e.ProcessName == "old.exe");
        Assert.Contains(res.Events, e => e.Type == ChangeType.NewConnection && e.ProcessName == "new.exe");
    }

    [Fact]
    public void First_and_last_observed_timestamps_are_maintained()
    {
        var c = C.Tcp("10.0.0.2", 5000, "8.8.8.8", 443, TcpState.Established, C.Proc(1));
        var t = Baselined();
        t.Update(C.T0.AddSeconds(1), new[] { c });
        var res = t.Update(C.T0.AddSeconds(9), new[] { c });
        Assert.Equal(C.T0.AddSeconds(1), res.Active[0].FirstObservedUtc);
        Assert.Equal(C.T0.AddSeconds(9), res.Active[0].LastObservedUtc);
    }

    [Fact]
    public void Large_snapshots_are_diffed_quickly_and_correctly()
    {
        var p = C.Proc(1);
        var snap1 = Enumerable.Range(0, 30000).Select(i => C.Tcp("10.0.0.2", 10000 + i % 50000, $"20.{i / 65025 % 250}.{i / 255 % 255}.{i % 255 + 1}", 443, TcpState.Established, p)).ToList();
        var t = Baselined(snap1.ToArray());
        var snap2 = snap1.Skip(100).ToList();
        snap2.Add(C.Tcp("10.0.0.2", 1, "9.9.9.9", 1, TcpState.Established, p));
        var sw = Stopwatch.StartNew();
        var res = t.Update(C.T0.AddSeconds(1), snap2);
        sw.Stop();
        Assert.Equal(100, res.Events.Count(e => e.Type == ChangeType.ConnectionClosed));
        Assert.Equal(1, res.Events.Count(e => e.Type == ChangeType.NewConnection));
        Assert.True(sw.ElapsedMilliseconds < 3000, $"took {sw.ElapsedMilliseconds} ms");
    }
}

public class MonitorTests
{
    private static NetworkMonitor Make(FakeSource src, FakeProcessApi? api = null) =>
        new(src, new ProcessResolver(api ?? new FakeProcessApi().Add(1, "a.exe")), new ConnectionTracker());

    private static ConnectionReadResult Result(params ConnectionRecord[] r) => new(r, Array.Empty<string>());
    private static ConnectionRecord Rec(int lp = 5000, int pid = 1) =>
        new(Protocol.Tcp, IPAddress.Parse("10.0.0.2"), lp, IPAddress.Parse("8.8.8.8"), 443, TcpState.Established, pid);

    [Fact]
    public async Task Poll_resolves_processes_and_raises_event()
    {
        var src = new FakeSource { Handler = _ => Result(Rec()) };
        var m = Make(src);
        MonitorCycleResult? seen = null;
        m.CycleCompleted += r => seen = r;
        var res = await m.PollOnceAsync();
        Assert.NotNull(res);
        Assert.Same(res, seen);
        Assert.Equal("a.exe", res!.Tracker.Active[0].Connection.Process.Name);
        Assert.NotNull(m.LastRefreshUtc);
    }

    [Fact]
    public async Task Api_failure_is_reported_and_monitor_recovers()
    {
        var fail = true;
        var src = new FakeSource { Handler = _ => fail ? throw new InvalidOperationException("boom") : Result(Rec()) };
        var m = Make(src);
        string? err = null;
        m.ErrorOccurred += e => err = e;
        Assert.Null(await m.PollOnceAsync());
        Assert.Equal("boom", err);
        Assert.Equal(1, m.ConsecutiveFailures);
        fail = false;
        Assert.NotNull(await m.PollOnceAsync());
        Assert.Equal(0, m.ConsecutiveFailures);
        Assert.Null(m.LastError);
    }

    [Fact]
    public async Task Process_resolution_failure_does_not_break_the_cycle()
    {
        var api = new FakeProcessApi { ThrowOnQuery = true };
        var m = Make(new FakeSource { Handler = _ => Result(Rec(pid: 55)) }, api);
        var res = await m.PollOnceAsync();
        Assert.NotNull(res);
        Assert.Single(res!.Tracker.Active);
    }

    [Fact]
    public async Task Overlapping_cycles_are_prevented()
    {
        var gate = new ManualResetEventSlim();
        var src = new FakeSource { Handler = _ => { gate.Wait(); return Result(Rec()); } };
        var m = Make(src);
        var first = m.PollOnceAsync();
        await Task.Delay(100);
        var second = await m.PollOnceAsync();
        Assert.Null(second);
        Assert.Equal(1, m.SkippedOverlaps);
        gate.Set();
        Assert.NotNull(await first);
        Assert.Equal(1, src.Calls);
    }

    [Fact]
    public async Task Cancellation_stops_polling_promptly()
    {
        var src = new FakeSource { Handler = _ => Result(Rec()) };
        var m = Make(src);
        m.Interval = TimeSpan.FromMilliseconds(250);
        m.Start();
        await Task.Delay(700);
        Assert.Equal(MonitorState.Running, m.State);
        var sw = Stopwatch.StartNew();
        await m.StopAsync();
        Assert.True(sw.ElapsedMilliseconds < 1500);
        Assert.Equal(MonitorState.Stopped, m.State);
        var calls = src.Calls;
        await Task.Delay(600);
        Assert.Equal(calls, src.Calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => m.PollOnceAsync(new CancellationToken(true)));
    }

    [Fact]
    public async Task Pause_and_resume_control_polling()
    {
        var src = new FakeSource { Handler = _ => Result(Rec()) };
        var m = Make(src);
        m.Interval = TimeSpan.FromMilliseconds(250);
        m.Start();
        await Task.Delay(400);
        m.Pause();
        await Task.Delay(300);
        var paused = src.Calls;
        await Task.Delay(700);
        Assert.Equal(paused, src.Calls);
        m.Resume();
        await Task.Delay(700);
        Assert.True(src.Calls > paused);
        await m.StopAsync();
    }

    [Fact]
    public async Task Options_are_passed_to_the_source()
    {
        MonitorOptions? got = null;
        var m = Make(new FakeSource { Handler = o => { got = o; return Result(); } });
        m.Options = new MonitorOptions(Tcp: false, IPv6: false);
        await m.PollOnceAsync();
        Assert.False(got!.Tcp);
        Assert.False(got.IPv6);
    }
}

public class FilterTests
{
    private static List<TrackedConnection> Sample() => new()
    {
        C.Tracked(C.Tcp("192.168.1.10", 51234, "142.250.1.1", 443, TcpState.Established, C.Proc(100, "chrome.exe"))),
        C.Tracked(C.Tcp("192.168.1.10", 51235, "104.16.0.1", 80, TcpState.TimeWait, C.Proc(100, "chrome.exe"))),
        C.Tracked(C.Listen(135, C.Proc(200, "svchost.exe"))),
        C.Tracked(C.Udp(5353, C.Proc(300, "mdns.exe"))),
        C.Tracked(new Connection(new ConnectionRecord(Protocol.Tcp, IPAddress.Parse("fe80::1"), 6000, IPAddress.Parse("2606:4700::1"), 443, TcpState.Established, 400), C.Proc(400, "discord.exe"))),
    };

    private static List<string> Names(ConnectionFilter f) => Sample().Where(t => f.Matches(t)).Select(t => t.Connection.Process.Name).ToList();

    [Fact] public void Empty_filter_matches_everything() => Assert.Equal(5, Names(new ConnectionFilter()).Count);

    [Fact]
    public void Process_filter_matches_by_name_and_pid()
    {
        Assert.Equal(2, Names(new ConnectionFilter { ProcessName = "chrome" }).Count);
        Assert.Equal(new[] { "svchost.exe" }, Names(new ConnectionFilter { Pid = 200 }));
        Assert.Equal(new[] { "discord.exe" }, Names(new ConnectionFilter { Search = "discord" }));
        Assert.Equal(2, Names(new ConnectionFilter { Search = "100" }).Count);
    }

    [Fact]
    public void Port_filters_match_local_and_remote_ports()
    {
        Assert.Equal(2, Names(new ConnectionFilter { RemotePort = 443 }).Count);
        Assert.Equal(new[] { "svchost.exe" }, Names(new ConnectionFilter { LocalPort = 135 }));
        Assert.Equal(new[] { "mdns.exe" }, Names(new ConnectionFilter { Search = "5353" }));
    }

    [Fact]
    public void Protocol_and_family_filters()
    {
        Assert.Equal(new[] { "mdns.exe" }, Names(new ConnectionFilter { Tcp = false }));
        Assert.Equal(4, Names(new ConnectionFilter { Udp = false }).Count);
        Assert.Equal(new[] { "discord.exe" }, Names(new ConnectionFilter { IPv4 = false }));
        Assert.Equal(4, Names(new ConnectionFilter { IPv6 = false }).Count);
    }

    [Fact]
    public void State_and_type_filters_combine()
    {
        Assert.Equal(new[] { "svchost.exe" }, Names(new ConnectionFilter { OnlyListening = true }));
        Assert.Equal(2, Names(new ConnectionFilter { OnlyEstablished = true }).Count);
        Assert.Equal(new[] { "chrome.exe" }, Names(new ConnectionFilter { States = { TcpState.TimeWait } }));
        Assert.Equal(new[] { "chrome.exe" }, Names(new ConnectionFilter { OnlyEstablished = true, ProcessName = "chrome" }));
    }

    [Fact]
    public void Presets_work_and_reset_clears()
    {
        Assert.Equal(new[] { "discord.exe" }, Names(FilterPresets.BuiltIn["IPv6 connections"]()));
        Assert.Equal(2, Names(FilterPresets.BuiltIn["Browser connections"]()).Count);
        var f = FilterPresets.BuiltIn["Listening ports"]();
        Assert.False(f.IsEmpty);
        f.Reset();
        Assert.True(f.IsEmpty);
    }

    [Fact]
    public void Newly_detected_preset_only_matches_post_baseline_connections()
    {
        var fresh = new TrackedConnection(C.Listen(1, C.Proc(1)), DateTime.UtcNow) { DetectedAfterBaseline = true };
        var old = new TrackedConnection(C.Listen(2, C.Proc(1)), DateTime.UtcNow);
        var f = FilterPresets.BuiltIn["Newly detected endpoints"]();
        Assert.True(f.Matches(fresh));
        Assert.False(f.Matches(old));
    }
}

public class AlertTests
{
    private static ConnectionEvent Ev(ChangeType type, string proc = "app.exe", string? remote = "8.8.8.8", int rport = 443, int lport = 5000, TcpState state = TcpState.Established)
        => new(C.T0, type, 1, proc, $@"C:\apps\{proc}", "1:0", null, Protocol.Tcp, "10.0.0.2", lport, remote, rport, state, null, "k");

    private static AlertEngine Engine(params AlertRule[] rules) { var e = new AlertEngine(); e.SetRules(rules); return e; }
    private static AlertRule R(AlertRuleType t, string v = "", bool on = true) => new(1, t.ToString(), t, v, on, AlertSeverity.Warning);

    [Fact] public void New_listening_port_rule_fires() =>
        Assert.Single(Engine(R(AlertRuleType.NewListeningPort)).Evaluate(Ev(ChangeType.NewListeningPort)));

    [Fact] public void Disabled_rules_do_not_fire() =>
        Assert.Empty(Engine(R(AlertRuleType.NewListeningPort, on: false)).Evaluate(Ev(ChangeType.NewListeningPort)));

    [Fact]
    public void New_remote_ip_and_new_process_rules_fire_on_their_events()
    {
        var e = Engine(R(AlertRuleType.NewRemoteIp), R(AlertRuleType.NewProcess));
        Assert.Single(e.Evaluate(Ev(ChangeType.NewRemoteIp)));
        Assert.Single(e.Evaluate(Ev(ChangeType.NewProcess)));
        Assert.Empty(e.Evaluate(Ev(ChangeType.NewConnection)));
    }

    [Fact]
    public void Blocked_ip_supports_exact_and_cidr()
    {
        var e = Engine(R(AlertRuleType.BlockedIp, "203.0.113.0/24, 1.2.3.4"));
        Assert.Single(e.Evaluate(Ev(ChangeType.NewConnection, remote: "203.0.113.77")));
        Assert.Single(e.Evaluate(Ev(ChangeType.NewConnection, remote: "1.2.3.4")));
        Assert.Empty(e.Evaluate(Ev(ChangeType.NewConnection, remote: "203.0.114.1")));
    }

    [Fact]
    public void Blocked_ip_supports_ipv6_cidr()
    {
        var e = Engine(R(AlertRuleType.BlockedIp, "2001:db8::/32"));
        Assert.Single(e.Evaluate(Ev(ChangeType.NewConnection, remote: "2001:db8:1::5")));
        Assert.Empty(e.Evaluate(Ev(ChangeType.NewConnection, remote: "2001:db9::5")));
    }

    [Fact]
    public void Blocked_port_supports_lists_and_ranges()
    {
        var e = Engine(R(AlertRuleType.BlockedPort, "23, 6660-6669"));
        Assert.Single(e.Evaluate(Ev(ChangeType.NewConnection, rport: 23)));
        Assert.Single(e.Evaluate(Ev(ChangeType.NewConnection, rport: 6667)));
        Assert.Empty(e.Evaluate(Ev(ChangeType.NewConnection, rport: 443)));
    }

    [Fact]
    public void Unexpected_state_rule_matches_configured_states()
    {
        var e = Engine(R(AlertRuleType.UnexpectedState, "CLOSE_WAIT,LAST_ACK"));
        Assert.Single(e.Evaluate(Ev(ChangeType.StateChanged, state: TcpState.CloseWait)));
        Assert.Empty(e.Evaluate(Ev(ChangeType.StateChanged, state: TcpState.Established)));
    }

    [Fact]
    public void Whitelists_suppress_alerts()
    {
        var e = Engine(R(AlertRuleType.NewProcess), R(AlertRuleType.BlockedPort, "443"),
                       R(AlertRuleType.WhitelistProcess, "trusted.exe"), R(AlertRuleType.WhitelistIp, "8.8.8.0/24"));
        Assert.Empty(e.Evaluate(Ev(ChangeType.NewProcess, proc: "TRUSTED.exe", remote: "1.1.1.1")));
        Assert.Empty(e.Evaluate(Ev(ChangeType.NewConnection, remote: "8.8.8.8")));
        Assert.Single(e.Evaluate(Ev(ChangeType.NewConnection, remote: "9.9.9.9")));
    }

    [Fact]
    public void Monitored_port_matches_local_or_remote()
    {
        var e = Engine(R(AlertRuleType.MonitoredPort, "3389"));
        Assert.Single(e.Evaluate(Ev(ChangeType.NewListeningPort, lport: 3389, remote: null, rport: 0)));
        Assert.Single(e.Evaluate(Ev(ChangeType.NewConnection, rport: 3389)));
        Assert.Empty(e.Evaluate(Ev(ChangeType.NewConnection, rport: 80)));
    }

    [Fact]
    public void Alert_messages_do_not_claim_maliciousness()
    {
        var a = Engine(R(AlertRuleType.NewRemoteIp)).Evaluate(Ev(ChangeType.NewRemoteIp)).Single();
        Assert.DoesNotContain("malicious", a.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class IpTests
{
    [Theory]
    [InlineData("8.8.8.8", IpCategory.Public)]
    [InlineData("10.1.2.3", IpCategory.Private)]
    [InlineData("172.16.0.1", IpCategory.Private)]
    [InlineData("172.32.0.1", IpCategory.Public)]
    [InlineData("192.168.0.5", IpCategory.Private)]
    [InlineData("127.0.0.1", IpCategory.Loopback)]
    [InlineData("::1", IpCategory.Loopback)]
    [InlineData("169.254.10.1", IpCategory.LinkLocal)]
    [InlineData("fe80::1", IpCategory.LinkLocal)]
    [InlineData("fd00::1", IpCategory.Private)]
    [InlineData("224.0.0.251", IpCategory.Reserved)]
    [InlineData("ff02::fb", IpCategory.Reserved)]
    [InlineData("100.64.0.1", IpCategory.Reserved)]
    [InlineData("0.0.0.0", IpCategory.Unspecified)]
    [InlineData("::", IpCategory.Unspecified)]
    [InlineData("2606:4700::1111", IpCategory.Public)]
    [InlineData("::ffff:192.168.1.1", IpCategory.Private)]
    public void Classification(string ip, IpCategory expected) => Assert.Equal(expected, IpClassifier.Classify(IPAddress.Parse(ip)));

    [Fact]
    public void Describe_reports_version()
    {
        Assert.Equal("IPv4", IpClassifier.Describe(IPAddress.Parse("1.1.1.1")).Version);
        Assert.Equal("IPv6", IpClassifier.Describe(IPAddress.Parse("2606:4700::1")).Version);
    }

    [Theory]
    [InlineData("[::1]", true)]
    [InlineData("fe80::1%12", true)]
    [InlineData("300.1.1.1", false)]
    [InlineData("", false)]
    public void TryParse_handles_brackets_scopes_and_garbage(string s, bool ok) => Assert.Equal(ok, IpClassifier.TryParse(s, out _));
}

public class GraphTests
{
    private static List<TrackedConnection> L(params TrackedConnection[] x) => x.ToList();
    private static (List<TrackedConnection> active, List<TrackedConnection> closed) Data(int procs, int remotesPer)
    {
        var list = new List<TrackedConnection>();
        for (var p = 1; p <= procs; p++)
        {
            var proc = C.Proc(p, $"p{p}.exe");
            for (var r = 0; r < remotesPer; r++)
                list.Add(C.Tracked(C.Tcp("10.0.0.2", 40000 + r, $"20.{p}.0.{r + 1}", 443, TcpState.Established, proc)));
            list.Add(C.Tracked(C.Listen(8000 + p, proc)));
        }
        return (list, L());
    }

    [Fact]
    public void Builds_computer_process_remote_structure()
    {
        var g = new NetworkGraph();
        var (a, c) = Data(3, 4);
        Assert.True(g.Update(a, c, new GraphOptions(), C.T0));
        Assert.Equal(1 + 3 + 12 + 3, g.Nodes.Count);
        Assert.Equal(3, g.Nodes.Values.Count(n => n.Kind == NodeKind.Process));
        Assert.Equal(12, g.Nodes.Values.Count(n => n.Kind == NodeKind.Remote));
        Assert.Equal(3 + 12 + 3, g.Edges.Count);
    }

    [Fact]
    public void Updates_are_incremental_and_keep_node_identity_and_position()
    {
        var g = new NetworkGraph();
        var (a, c) = Data(2, 2);
        g.Update(a, c, new GraphOptions(), C.T0);
        var node = g.Nodes.Values.First(n => n.Kind == NodeKind.Process);
        node.X = 123; node.Y = 456;
        Assert.False(g.Update(a, c, new GraphOptions(), C.T0.AddSeconds(1)));
        Assert.Same(node, g.Nodes[node.Id]);
        Assert.Equal(123, node.X);
        a.Add(C.Tracked(C.Tcp("10.0.0.2", 1, "9.9.9.9", 1, TcpState.Established, node.Process!)));
        Assert.True(g.Update(a, c, new GraphOptions(), C.T0.AddSeconds(2)));
        Assert.Same(node, g.Nodes[node.Id]);
        Assert.Equal(123, node.X);
    }

    [Fact]
    public void Removed_connections_remove_nodes_and_group_by_ip_merges_ports()
    {
        var g = new NetworkGraph();
        var p = C.Proc(1);
        var a = new List<TrackedConnection>
        {
            C.Tracked(C.Tcp("10.0.0.2", 1, "9.9.9.9", 443, TcpState.Established, p)),
            C.Tracked(C.Tcp("10.0.0.2", 2, "9.9.9.9", 80, TcpState.Established, p)),
        };
        g.Update(a, L(), new GraphOptions(GroupRemoteByIp: true), C.T0);
        Assert.Single(g.Nodes.Values, n => n.Kind == NodeKind.Remote);
        Assert.Equal(2, g.Nodes["r:9.9.9.9"].ConnectionCount);
        g.Update(a, L(), new GraphOptions(GroupRemoteByIp: false), C.T0);
        Assert.Equal(2, g.Nodes.Values.Count(n => n.Kind == NodeKind.Remote));
        g.Update(L(), L(), new GraphOptions(), C.T0);
        Assert.Single(g.Nodes);
    }

    [Fact]
    public void Statuses_new_closed_and_warning()
    {
        var g = new NetworkGraph();
        var p = C.Proc(1);
        var fresh = new TrackedConnection(C.Tcp("10.0.0.2", 1, "9.9.9.9", 443, TcpState.Established, p), C.T0) { DetectedAfterBaseline = true };
        var closed = new TrackedConnection(C.Tcp("10.0.0.2", 2, "7.7.7.7", 443, TcpState.Established, p), C.T0.AddSeconds(-60)) { ClosedUtc = C.T0 };
        var warn = new TrackedConnection(C.Tcp("10.0.0.2", 3, "6.6.6.6", 443, TcpState.Established, p), C.T0.AddSeconds(-60));
        g.Update(L(fresh, warn), L(closed), new GraphOptions(WarningRemoteIps: new HashSet<string> { "6.6.6.6" }), C.T0.AddSeconds(2));
        Assert.Equal(NodeStatus.New, g.Nodes["r:9.9.9.9"].Status);
        Assert.Equal(NodeStatus.Closed, g.Nodes["r:7.7.7.7"].Status);
        Assert.Equal(NodeStatus.Warning, g.Nodes["r:6.6.6.6"].Status);
        g.Update(L(fresh, warn), L(closed), new GraphOptions(ShowClosed: false), C.T0.AddSeconds(2));
        Assert.False(g.Nodes.ContainsKey("r:7.7.7.7"));
    }

    [Theory]
    [InlineData(GraphLayoutMode.ForceDirected)]
    [InlineData(GraphLayoutMode.Hierarchical)]
    [InlineData(GraphLayoutMode.Circular)]
    [InlineData(GraphLayoutMode.GroupedByProcess)]
    public void All_layouts_produce_finite_non_overlapping_positions(GraphLayoutMode mode)
    {
        var g = new NetworkGraph();
        var (a, c) = Data(8, 6);
        g.Update(a, c, new GraphOptions(), C.T0);
        g.SetMode(mode);
        g.Settle(800);
        var nodes = g.Nodes.Values.ToList();
        Assert.All(nodes, n => { Assert.True(double.IsFinite(n.X) && double.IsFinite(n.Y)); });
        var overlaps = 0;
        for (var i = 0; i < nodes.Count; i++)
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var d = Math.Sqrt(Math.Pow(nodes[i].X - nodes[j].X, 2) + Math.Pow(nodes[i].Y - nodes[j].Y, 2));
                if (d < (nodes[i].Radius + nodes[j].Radius) * 0.8) overlaps++;
            }
        Assert.True(overlaps <= nodes.Count / 10, $"{mode}: {overlaps} overlapping pairs among {nodes.Count} nodes");
    }

    [Fact]
    public void Reset_layout_unpins_nodes_and_restarts_simulation()
    {
        var g = new NetworkGraph();
        var (a, c) = Data(2, 2);
        g.Update(a, c, new GraphOptions(), C.T0);
        g.Settle();
        var n = g.Nodes.Values.First(x => x.Kind == NodeKind.Process);
        n.Pinned = true;
        g.ResetLayout();
        Assert.False(n.Pinned);
        Assert.True(g.Step());
    }

    [Fact]
    public void Large_graph_updates_stay_fast()
    {
        var g = new NetworkGraph();
        var (a, c) = Data(60, 50);
        var sw = Stopwatch.StartNew();
        g.Update(a, c, new GraphOptions(), C.T0);
        for (var i = 0; i < 10; i++) g.Step();
        g.Update(a, c, new GraphOptions(), C.T0.AddSeconds(1));
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");
    }
}

public class SettingsTests
{
    [Fact]
    public void Settings_round_trip_through_dictionary()
    {
        var s = new AppSettings { RefreshIntervalMs = 5000, DarkTheme = false, GraphLayout = GraphLayoutMode.Circular, AccentColor = "#112233", RetentionDays = 7, LocalOnlyMode = false };
        var back = AppSettings.From(s.ToDictionary());
        Assert.Equal(5000, back.RefreshIntervalMs);
        Assert.False(back.DarkTheme);
        Assert.Equal(GraphLayoutMode.Circular, back.GraphLayout);
        Assert.Equal("#112233", back.AccentColor);
        Assert.Equal(7, back.RetentionDays);
    }

    [Fact]
    public void Invalid_values_fall_back_to_defaults_and_privacy_defaults_are_conservative()
    {
        var s = AppSettings.From(new Dictionary<string, string> { ["RefreshIntervalMs"] = "abc", ["GraphLayout"] = "nope", ["RetentionDays"] = "99999999999" });
        Assert.Equal(1000, s.RefreshIntervalMs);
        Assert.Equal(GraphLayoutMode.ForceDirected, s.GraphLayout);
        Assert.False(s.ReverseDnsAllowed);
        Assert.True(s.LocalOnlyMode);
    }

    [Fact]
    public void Refresh_interval_is_clamped()
    {
        Assert.Equal(250, AppSettings.From(new Dictionary<string, string> { ["RefreshIntervalMs"] = "1" }).RefreshIntervalMs);
    }
}
