using System.Net;
using PortVisualizer.Core.Models;
using PortVisualizer.Core.Native;
using PortVisualizer.Core.Processes;

namespace PortVisualizer.Tests;

public static class TableBuilder
{
    private static void Port(List<byte> b, int port) { b.Add((byte)(port >> 8)); b.Add((byte)port); b.Add(0); b.Add(0); }
    private static void U32(List<byte> b, uint v) => b.AddRange(BitConverter.GetBytes(v));

    public static byte[] Tcp4(params (string l, int lp, string r, int rp, TcpState s, int pid)[] rows)
    {
        var b = new List<byte>(); U32(b, (uint)rows.Length);
        foreach (var x in rows)
        {
            U32(b, (uint)x.s); b.AddRange(IPAddress.Parse(x.l).GetAddressBytes()); Port(b, x.lp);
            b.AddRange(IPAddress.Parse(x.r).GetAddressBytes()); Port(b, x.rp); U32(b, (uint)x.pid);
        }
        return b.ToArray();
    }

    public static byte[] Tcp6(params (string l, uint lscope, int lp, string r, int rp, TcpState s, int pid)[] rows)
    {
        var b = new List<byte>(); U32(b, (uint)rows.Length);
        foreach (var x in rows)
        {
            b.AddRange(IPAddress.Parse(x.l).GetAddressBytes()); U32(b, x.lscope); Port(b, x.lp);
            b.AddRange(IPAddress.Parse(x.r).GetAddressBytes()); U32(b, 0); Port(b, x.rp);
            U32(b, (uint)x.s); U32(b, (uint)x.pid);
        }
        return b.ToArray();
    }

    public static byte[] Udp4(params (string l, int lp, int pid)[] rows)
    {
        var b = new List<byte>(); U32(b, (uint)rows.Length);
        foreach (var x in rows) { b.AddRange(IPAddress.Parse(x.l).GetAddressBytes()); Port(b, x.lp); U32(b, (uint)x.pid); }
        return b.ToArray();
    }

    public static byte[] Udp6(params (string l, int lp, int pid)[] rows)
    {
        var b = new List<byte>(); U32(b, (uint)rows.Length);
        foreach (var x in rows) { b.AddRange(IPAddress.Parse(x.l).GetAddressBytes()); U32(b, 0); Port(b, x.lp); U32(b, (uint)x.pid); }
        return b.ToArray();
    }
}

public sealed class FakeProcessApi : IProcessApi
{
    public Dictionary<int, RawProcess> Processes { get; } = new();
    public int QueryCalls;
    public bool ThrowOnQuery;

    public DateTime? GetStartTimeUtc(int pid) => Processes.TryGetValue(pid, out var p) ? p.StartTimeUtc : null;

    public RawProcess? Query(int pid)
    {
        QueryCalls++;
        if (ThrowOnQuery) throw new UnauthorizedAccessException();
        return Processes.TryGetValue(pid, out var p) ? p : null;
    }

    public FakeProcessApi Add(int pid, string name, DateTime? start = null, string? path = null)
    {
        Processes[pid] = new RawProcess(name, path ?? $@"C:\apps\{name}", start ?? new DateTime(2026, 1, 1, 0, 0, pid % 60, DateTimeKind.Utc), false);
        return this;
    }
}

public sealed class FakeSource : IConnectionSource
{
    public Func<MonitorOptions, ConnectionReadResult> Handler { get; set; } = _ => new ConnectionReadResult(Array.Empty<ConnectionRecord>(), Array.Empty<string>());
    public int Calls;
    public ConnectionReadResult Read(MonitorOptions options) { Interlocked.Increment(ref Calls); return Handler(options); }
}

public static class C
{
    public static readonly DateTime T0 = new(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);

    public static ProcessInfo Proc(int pid, string name = "app.exe", int startSec = 0, string? path = null) =>
        new(pid, name, path ?? $@"C:\apps\{name}", T0.AddSeconds(startSec));

    public static Connection Tcp(string local, int lp, string remote, int rp, TcpState state, ProcessInfo p) =>
        new(new ConnectionRecord(Protocol.Tcp, IPAddress.Parse(local), lp, IPAddress.Parse(remote), rp, state, p.Pid), p);

    public static Connection Listen(int port, ProcessInfo p, string ip = "0.0.0.0") =>
        new(new ConnectionRecord(Protocol.Tcp, IPAddress.Parse(ip), port, IPAddress.Any, 0, TcpState.Listen, p.Pid), p);

    public static Connection Udp(int port, ProcessInfo p, string ip = "0.0.0.0") =>
        new(new ConnectionRecord(Protocol.Udp, IPAddress.Parse(ip), port, null, 0, TcpState.None, p.Pid), p);

    public static TrackedConnection Tracked(Connection c, DateTime? first = null) => new(c, first ?? T0);
}
