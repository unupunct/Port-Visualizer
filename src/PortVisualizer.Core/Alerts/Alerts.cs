using System.Net;
using PortVisualizer.Core.Models;

namespace PortVisualizer.Core.Alerts;

public enum AlertRuleType
{
    NewListeningPort, NewRemoteIp, NewProcess, UnexpectedState, BlockedIp, BlockedPort, MonitoredPort,
    WhitelistProcess, WhitelistIp,
}

public enum AlertSeverity { Info, Warning, Critical }

public sealed record AlertRule(long Id, string Name, AlertRuleType Type, string Value, bool IsEnabled, AlertSeverity Severity)
{
    public bool IsWhitelist => Type is AlertRuleType.WhitelistProcess or AlertRuleType.WhitelistIp;
}

public sealed record Alert(DateTime TimestampUtc, AlertRule Rule, ConnectionEvent Event, string Message)
{
    public AlertSeverity Severity => Rule.Severity;
    public string Time => TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string SeverityText => Rule.Severity.ToString();
}

public static class AlertDefaults
{
    public static IReadOnlyList<AlertRule> Rules { get; } = new[]
    {
        new AlertRule(0, "New listening port", AlertRuleType.NewListeningPort, "", true, AlertSeverity.Warning),
        new AlertRule(0, "New remote IP address", AlertRuleType.NewRemoteIp, "", false, AlertSeverity.Info),
        new AlertRule(0, "New process using the network", AlertRuleType.NewProcess, "", true, AlertSeverity.Info),
        new AlertRule(0, "Unexpected connection state", AlertRuleType.UnexpectedState, "CLOSE_WAIT,CLOSING,LAST_ACK", false, AlertSeverity.Info),
    };
}

/// <summary>
/// Evaluates user-defined rules against connection events. Rules describe what the user wants to be told about;
/// a match is not a claim that anything is malicious.
/// </summary>
public sealed class AlertEngine
{
    private IReadOnlyList<AlertRule> _rules = Array.Empty<AlertRule>();

    public void SetRules(IEnumerable<AlertRule> rules) => _rules = rules.Where(r => r.IsEnabled).ToList();

    public List<Alert> Evaluate(ConnectionEvent e)
    {
        var rules = _rules;
        var result = new List<Alert>();
        if (rules.Count == 0) return result;
        if (IsWhitelisted(rules, e)) return result;

        foreach (var r in rules)
        {
            if (r.IsWhitelist) continue;
            var message = Match(r, e);
            if (message is not null) result.Add(new Alert(e.TimestampUtc, r, e, message));
        }
        return result;
    }

    private static bool IsWhitelisted(IReadOnlyList<AlertRule> rules, ConnectionEvent e)
    {
        foreach (var r in rules)
        {
            if (r.Type == AlertRuleType.WhitelistProcess)
            {
                foreach (var v in Split(r.Value))
                    if (e.ProcessName.Equals(v, StringComparison.OrdinalIgnoreCase)
                        || (e.ProcessPath is not null && e.ProcessPath.Equals(v, StringComparison.OrdinalIgnoreCase)))
                        return true;
            }
            else if (r.Type == AlertRuleType.WhitelistIp && e.RemoteAddress is not null)
            {
                if (IpMatcher.MatchesAny(r.Value, e.RemoteAddress)) return true;
            }
        }
        return false;
    }

    private static string? Match(AlertRule r, ConnectionEvent e)
    {
        var who = $"{e.ProcessName} (PID {e.Pid})";
        switch (r.Type)
        {
            case AlertRuleType.NewListeningPort when e.Type == ChangeType.NewListeningPort:
                return $"{who} started listening on {e.Protocol.ToString().ToUpperInvariant()} {e.Local}";
            case AlertRuleType.NewRemoteIp when e.Type == ChangeType.NewRemoteIp:
                return $"First connection to {e.RemoteAddress} observed from {who}";
            case AlertRuleType.NewProcess when e.Type == ChangeType.NewProcess:
                return $"{who} is using the network for the first time";
            case AlertRuleType.UnexpectedState when e.Type is ChangeType.StateChanged or ChangeType.NewConnection:
                foreach (var s in Split(r.Value))
                    if (TcpStateExtensions.TryParseDisplay(s, out var st) && st == e.State)
                        return $"{who} connection {e.Endpoints} entered state {e.State.ToDisplay()}";
                return null;
            case AlertRuleType.BlockedIp when e.Type == ChangeType.NewConnection && e.RemoteAddress is not null
                                               && IpMatcher.MatchesAny(r.Value, e.RemoteAddress):
                return $"{who} connected to listed IP {e.RemoteAddress}";
            case AlertRuleType.BlockedPort when e.Type == ChangeType.NewConnection && PortMatcher.Matches(r.Value, e.RemotePort):
                return $"{who} connected to listed port {e.RemotePort} ({e.Endpoints})";
            case AlertRuleType.MonitoredPort when e.Type is ChangeType.NewConnection or ChangeType.NewListeningPort or ChangeType.NewUdpEndpoint:
                if (PortMatcher.Matches(r.Value, e.LocalPort) || (e.RemoteAddress is not null && PortMatcher.Matches(r.Value, e.RemotePort)))
                    return $"Activity on monitored port: {who} {e.Endpoints}";
                return null;
            default:
                return null;
        }
    }

    internal static IEnumerable<string> Split(string? v) =>
        (v ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public static class PortMatcher
{
    /// <summary>Accepts lists and ranges such as "22,3389,8000-8100".</summary>
    public static bool Matches(string spec, int port)
    {
        foreach (var part in AlertEngine.Split(spec))
        {
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var lo) && int.TryParse(part[(dash + 1)..], out var hi))
            { if (port >= lo && port <= hi) return true; }
            else if (int.TryParse(part, out var single) && single == port) return true;
        }
        return false;
    }

    public static bool IsValid(string spec) =>
        AlertEngine.Split(spec).Any() && AlertEngine.Split(spec).All(p =>
        {
            var d = p.IndexOf('-');
            if (d > 0) return int.TryParse(p[..d], out var a) && int.TryParse(p[(d + 1)..], out var b) && a is >= 0 and <= 65535 && b >= a && b <= 65535;
            return int.TryParse(p, out var s) && s is >= 0 and <= 65535;
        });
}

public static class IpMatcher
{
    /// <summary>Accepts a comma-separated list of IPs and CIDR blocks (IPv4 or IPv6).</summary>
    public static bool MatchesAny(string spec, string ip)
    {
        if (!IpClassifier.TryParse(ip, out var addr)) return false;
        foreach (var part in AlertEngine.Split(spec))
            if (Matches(part, addr)) return true;
        return false;
    }

    public static bool Matches(string pattern, IPAddress addr)
    {
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        var slash = pattern.IndexOf('/');
        var netText = slash < 0 ? pattern : pattern[..slash];
        if (!IpClassifier.TryParse(netText, out var net)) return false;
        if (net.IsIPv4MappedToIPv6) net = net.MapToIPv4();
        if (net.AddressFamily != addr.AddressFamily) return false;
        var a = addr.GetAddressBytes();
        var n = net.GetAddressBytes();
        var bits = slash < 0 ? a.Length * 8 : int.TryParse(pattern[(slash + 1)..], out var b) ? b : -1;
        if (bits < 0 || bits > a.Length * 8) return false;
        for (var i = 0; i < a.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
            if ((a[i] & mask) != (n[i] & mask)) return false;
        }
        return true;
    }
}
