using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using PortVisualizer.Core.Models;

namespace PortVisualizer.Core.Native;

/// <summary>Pure parsers for the IP Helper owner-PID tables. Operate on byte buffers so they can be unit tested.</summary>
public static class TableParser
{
    public const int Tcp4RowSize = 24, Tcp6RowSize = 56, Udp4RowSize = 12, Udp6RowSize = 28;

    // Port is a DWORD with the port in network byte order in the low 16 bits.
    private static int Port(ReadOnlySpan<byte> b, int o) => (b[o] << 8) | b[o + 1];
    private static uint U32(ReadOnlySpan<byte> b, int o) => BitConverter.ToUInt32(b.Slice(o, 4));

    private static int Count(ReadOnlySpan<byte> buffer, int rowSize)
    {
        if (buffer.Length < 4) return 0;
        var n = (int)Math.Min(U32(buffer, 0), int.MaxValue);
        var max = (buffer.Length - 4) / rowSize;
        return Math.Min(n, max); // tolerate truncated buffers
    }

    private static IPAddress V4(ReadOnlySpan<byte> b, int o) => new(b.Slice(o, 4));

    private static IPAddress V6(ReadOnlySpan<byte> b, int o, uint scope) => new(b.Slice(o, 16), scope);

    public static List<ConnectionRecord> ParseTcp4(ReadOnlySpan<byte> b)
    {
        var n = Count(b, Tcp4RowSize);
        var list = new List<ConnectionRecord>(n);
        for (var i = 0; i < n; i++)
        {
            var o = 4 + i * Tcp4RowSize;
            var state = (TcpState)U32(b, o);
            list.Add(new ConnectionRecord(Protocol.Tcp, V4(b, o + 4), Port(b, o + 8),
                V4(b, o + 12), Port(b, o + 16), state, (int)U32(b, o + 20)));
        }
        return list;
    }

    public static List<ConnectionRecord> ParseTcp6(ReadOnlySpan<byte> b)
    {
        var n = Count(b, Tcp6RowSize);
        var list = new List<ConnectionRecord>(n);
        for (var i = 0; i < n; i++)
        {
            var o = 4 + i * Tcp6RowSize;
            var local = V6(b, o, U32(b, o + 16));
            var lport = Port(b, o + 20);
            var remote = V6(b, o + 24, U32(b, o + 40));
            var rport = Port(b, o + 44);
            var state = (TcpState)U32(b, o + 48);
            list.Add(new ConnectionRecord(Protocol.Tcp, local, lport, remote, rport, state, (int)U32(b, o + 52)));
        }
        return list;
    }

    public static List<ConnectionRecord> ParseUdp4(ReadOnlySpan<byte> b)
    {
        var n = Count(b, Udp4RowSize);
        var list = new List<ConnectionRecord>(n);
        for (var i = 0; i < n; i++)
        {
            var o = 4 + i * Udp4RowSize;
            list.Add(new ConnectionRecord(Protocol.Udp, V4(b, o), Port(b, o + 4), null, 0, TcpState.None, (int)U32(b, o + 8)));
        }
        return list;
    }

    public static List<ConnectionRecord> ParseUdp6(ReadOnlySpan<byte> b)
    {
        var n = Count(b, Udp6RowSize);
        var list = new List<ConnectionRecord>(n);
        for (var i = 0; i < n; i++)
        {
            var o = 4 + i * Udp6RowSize;
            list.Add(new ConnectionRecord(Protocol.Udp, V6(b, o, U32(b, o + 16)), Port(b, o + 20), null, 0, TcpState.None, (int)U32(b, o + 24)));
        }
        return list;
    }
}

/// <summary>Which tables to read.</summary>
public sealed record MonitorOptions(bool Tcp = true, bool Udp = true, bool IPv4 = true, bool IPv6 = true, bool IncludeListening = true)
{
    public static MonitorOptions Default { get; } = new();
}

public sealed record ConnectionReadResult(IReadOnlyList<ConnectionRecord> Records, IReadOnlyList<string> Warnings);

public interface IConnectionSource
{
    /// <summary>Reads the current connection tables. Throws if every requested table failed.</summary>
    ConnectionReadResult Read(MonitorOptions options);
}

/// <summary>Reads connections through GetExtendedTcpTable / GetExtendedUdpTable.</summary>
public sealed class WindowsConnectionSource : IConnectionSource
{
    private const int AfInet = 2, AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5, UdpTableOwnerPid = 1;
    private const int ErrorInsufficientBuffer = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedUdpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, int reserved);

    private delegate int TableFn(IntPtr table, ref int size, bool sort, int af, int tableClass, int reserved);

    public ConnectionReadResult Read(MonitorOptions o)
    {
        var records = new List<ConnectionRecord>();
        var warnings = new List<string>();
        var attempted = 0;

        void Run(string name, TableFn fn, int af, int cls, Func<byte[], List<ConnectionRecord>> parse)
        {
            attempted++;
            try
            {
                var buf = Fetch(fn, af, cls);
                var rows = parse(buf);
                if (!o.IncludeListening) rows.RemoveAll(r => r.Protocol == Protocol.Tcp && r.State == TcpState.Listen);
                records.AddRange(rows);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                warnings.Add($"{name}: {ex.Message}");
            }
        }

        if (o.Tcp && o.IPv4) Run("TCPv4", GetExtendedTcpTable, AfInet, TcpTableOwnerPidAll, b => TableParser.ParseTcp4(b));
        if (o.Tcp && o.IPv6) Run("TCPv6", GetExtendedTcpTable, AfInet6, TcpTableOwnerPidAll, b => TableParser.ParseTcp6(b));
        if (o.Udp && o.IPv4) Run("UDPv4", GetExtendedUdpTable, AfInet, UdpTableOwnerPid, b => TableParser.ParseUdp4(b));
        if (o.Udp && o.IPv6) Run("UDPv6", GetExtendedUdpTable, AfInet6, UdpTableOwnerPid, b => TableParser.ParseUdp6(b));

        if (attempted > 0 && warnings.Count == attempted)
            throw new InvalidOperationException("All connection table reads failed: " + string.Join("; ", warnings));
        return new ConnectionReadResult(records, warnings);
    }

    private static byte[] Fetch(TableFn fn, int af, int cls)
    {
        var size = 32 * 1024;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                var rc = fn(ptr, ref size, false, af, cls, 0);
                if (rc == 0)
                {
                    var bytes = new byte[size];
                    Marshal.Copy(ptr, bytes, 0, size);
                    return bytes;
                }
                if (rc != ErrorInsufficientBuffer) throw new Win32Exception(rc);
                size += size / 4 + 4096; // the table can grow between calls
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }
        throw new InvalidOperationException("Connection table kept growing; giving up this cycle.");
    }
}
