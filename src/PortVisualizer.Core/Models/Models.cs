using System.Net;
using System.Net.Sockets;

namespace PortVisualizer.Core.Models;

public enum Protocol { Tcp, Udp }

/// <summary>Values match the Windows MIB_TCP_STATE enumeration.</summary>
public enum TcpState
{
    Unknown = 0,
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12,
    /// <summary>UDP endpoints have no connection state.</summary>
    None = 100,
}

public static class TcpStateExtensions
{
    public static string ToDisplay(this TcpState s) => s switch
    {
        TcpState.Listen => "LISTENING",
        TcpState.SynSent => "SYN_SENT",
        TcpState.SynReceived => "SYN_RECEIVED",
        TcpState.Established => "ESTABLISHED",
        TcpState.FinWait1 => "FIN_WAIT_1",
        TcpState.FinWait2 => "FIN_WAIT_2",
        TcpState.CloseWait => "CLOSE_WAIT",
        TcpState.Closing => "CLOSING",
        TcpState.LastAck => "LAST_ACK",
        TcpState.TimeWait => "TIME_WAIT",
        TcpState.Closed => "CLOSED",
        TcpState.DeleteTcb => "DELETE_TCB",
        TcpState.None => "-",
        _ => "UNKNOWN",
    };

    public static bool TryParseDisplay(string? text, out TcpState state)
    {
        state = TcpState.Unknown;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Replace(' ', '_').ToUpperInvariant();
        foreach (var v in Enum.GetValues<TcpState>())
        {
            if (v.ToDisplay() == t || v.ToString().Equals(t.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
            {
                state = v;
                return true;
            }
        }
        if (t == "LISTEN") { state = TcpState.Listen; return true; }
        return false;
    }
}

/// <summary>A raw row from the IP Helper tables.</summary>
public sealed record ConnectionRecord(
    Protocol Protocol,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress? RemoteAddress,
    int RemotePort,
    TcpState State,
    int Pid)
{
    public bool IsIPv6 => LocalAddress.AddressFamily == AddressFamily.InterNetworkV6;
}

public enum SignatureStatus { NotChecked, Unknown, Valid, Invalid, NoEmbeddedSignature }

/// <summary>
/// Describes a process. Identity is the (PID, start time) pair because Windows reuses PIDs.
/// </summary>
public sealed class ProcessInfo
{
    public ProcessInfo(int pid, string name, string? path, DateTime? startTimeUtc, bool accessDenied = false)
    {
        Pid = pid;
        Name = name;
        Path = path;
        StartTimeUtc = startTimeUtc;
        AccessDenied = accessDenied;
    }

    public int Pid { get; }
    public string Name { get; }
    public string? Path { get; }
    public DateTime? StartTimeUtc { get; }
    public bool AccessDenied { get; }
    public string? Publisher { get; set; }
    public SignatureStatus Signature { get; set; } = SignatureStatus.NotChecked;

    /// <summary>Unique identity that survives PID reuse.</summary>
    public string Key => $"{Pid}:{StartTimeUtc?.Ticks ?? 0}";

    public string SignatureDisplay => Signature switch
    {
        SignatureStatus.Valid => "Valid signature",
        SignatureStatus.Invalid => "Signature invalid / untrusted",
        SignatureStatus.NoEmbeddedSignature => "No embedded signature (may be catalog-signed)",
        SignatureStatus.Unknown => "Unavailable",
        _ => "Not checked",
    };

    public static ProcessInfo Missing(int pid) => new(pid, pid == 0 ? "System (no owner)" : $"<pid {pid} exited>", null, null);
}

/// <summary>A connection joined with its owning process.</summary>
public sealed record Connection(ConnectionRecord Record, ProcessInfo Process)
{
    public Protocol Protocol => Record.Protocol;
    public TcpState State => Record.State;
    public bool IsListening => Record.Protocol == Protocol.Tcp && Record.State == TcpState.Listen;
    public bool IsUdp => Record.Protocol == Protocol.Udp;

    private string? _la, _ra, _key; private bool _raSet;
    public string LocalAddress => _la ??= Record.LocalAddress.ToString();
    public int LocalPort => Record.LocalPort;
    public string? RemoteAddress { get { if (!_raSet) { _ra = Record.RemoteAddress is null || IsListening ? null : Record.RemoteAddress.ToString(); _raSet = true; } return _ra; } }
    public int RemotePort => RemoteAddress is null ? 0 : Record.RemotePort;
    public bool HasRemote => RemoteAddress is not null && !Record.RemoteAddress!.Equals(IPAddress.Any) && !Record.RemoteAddress.Equals(IPAddress.IPv6Any);

    /// <summary>
    /// Stable identity of a connection between polls. Listening sockets and UDP endpoints include the owning
    /// process identity (several processes may share a port); established TCP is unique by its 4-tuple.
    /// </summary>
    public string Key
    {
        get
        {
            if (_key is not null) return _key;
            var proto = Record.Protocol == Protocol.Tcp ? "T" : "U";
            var tuple = $"{proto}|{LocalAddress}|{LocalPort}|{RemoteAddress}|{RemotePort}";
            return _key = Record.Protocol == Protocol.Udp || IsListening ? $"{tuple}|{Process.Key}" : tuple;
        }
    }

    public string LocalEndpoint => Format(LocalAddress, LocalPort);
    public string? RemoteEndpoint => HasRemote ? Format(RemoteAddress!, RemotePort) : null;

    public static string Format(string ip, int port) => ip.Contains(':') ? $"[{ip}]:{port}" : $"{ip}:{port}";
}

/// <summary>A connection plus how long we have been observing it.</summary>
public sealed class TrackedConnection
{
    public TrackedConnection(Connection connection, DateTime firstObservedUtc)
    {
        Connection = connection;
        FirstObservedUtc = firstObservedUtc;
        LastObservedUtc = firstObservedUtc;
    }

    public Connection Connection { get; internal set; }
    public DateTime FirstObservedUtc { get; }
    public DateTime LastObservedUtc { get; internal set; }
    public DateTime? ClosedUtc { get; set; }
    public bool IsClosed => ClosedUtc.HasValue;
    /// <summary>True when detected after the initial baseline snapshot.</summary>
    public bool DetectedAfterBaseline { get; set; }
    public string Key => Connection.Key;
}

public enum ChangeType
{
    NewConnection,
    ConnectionClosed,
    StateChanged,
    NewUdpEndpoint,
    UdpEndpointRemoved,
    NewListeningPort,
    ListeningPortClosed,
    NewRemoteIp,
    NewProcess,
}

public sealed record ConnectionEvent(
    DateTime TimestampUtc,
    ChangeType Type,
    int Pid,
    string ProcessName,
    string? ProcessPath,
    string ProcessKey,
    DateTime? ProcessStartUtc,
    Protocol Protocol,
    string LocalAddress,
    int LocalPort,
    string? RemoteAddress,
    int RemotePort,
    TcpState State,
    TcpState? PreviousState,
    string ConnectionKey)
{
    public long Id { get; init; }

    public string TypeDisplay => Type switch
    {
        ChangeType.NewConnection => "NEW CONNECTION",
        ChangeType.ConnectionClosed => "CONNECTION CLOSED",
        ChangeType.StateChanged => "STATE CHANGED",
        ChangeType.NewUdpEndpoint => "NEW UDP ENDPOINT",
        ChangeType.UdpEndpointRemoved => "UDP ENDPOINT REMOVED",
        ChangeType.NewListeningPort => "NEW LISTENING PORT",
        ChangeType.ListeningPortClosed => "LISTENING PORT CLOSED",
        ChangeType.NewRemoteIp => "NEW REMOTE IP",
        ChangeType.NewProcess => "NEW PROCESS",
        _ => Type.ToString(),
    };

    public string Local => Connection.Format(LocalAddress, LocalPort);
    public string? Remote => RemoteAddress is null ? null : Connection.Format(RemoteAddress, RemotePort);
    public string Endpoints => Remote is null ? Local : $"{Local} -> {Remote}";

    public string StateDisplay => Protocol == Protocol.Udp
        ? "UDP"
        : PreviousState is { } p && p != State ? $"TCP {p.ToDisplay()} -> {State.ToDisplay()}" : $"TCP {State.ToDisplay()}";

    public string Description => $"[{TimestampUtc.ToLocalTime():HH:mm:ss}] {TypeDisplay}  {ProcessName}  {Endpoints}  {StateDisplay}";
}
