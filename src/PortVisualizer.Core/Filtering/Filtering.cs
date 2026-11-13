using System.Net;
using PortVisualizer.Core.Models;

namespace PortVisualizer.Core.Filtering;

/// <summary>Combinable filter. All specified criteria must match (logical AND).</summary>
public sealed class ConnectionFilter
{
    public string? Search { get; set; }
    public string? ProcessName { get; set; }
    public int? Pid { get; set; }
    public string? ExecutablePath { get; set; }
    public string? RemoteIp { get; set; }
    public string? LocalIp { get; set; }
    public int? RemotePort { get; set; }
    public int? LocalPort { get; set; }
    public bool Tcp { get; set; } = true;
    public bool Udp { get; set; } = true;
    public bool IPv4 { get; set; } = true;
    public bool IPv6 { get; set; } = true;
    public HashSet<TcpState> States { get; set; } = new();
    public bool OnlyListening { get; set; }
    public bool OnlyEstablished { get; set; }
    public bool OnlyNewlyDetected { get; set; }
    public bool OnlyUnknownApplications { get; set; }
    public DateTime? ObservedFromUtc { get; set; }
    public DateTime? ObservedToUtc { get; set; }
    public HashSet<string> BrowserNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Search is null && ProcessName is null && Pid is null && ExecutablePath is null && RemoteIp is null
        && LocalIp is null && RemotePort is null && LocalPort is null && Tcp && Udp && IPv4 && IPv6 && States.Count == 0
        && !OnlyListening && !OnlyEstablished && !OnlyNewlyDetected && !OnlyUnknownApplications
        && ObservedFromUtc is null && ObservedToUtc is null && BrowserNames.Count == 0;

    public ConnectionFilter Clone()
    {
        var c = (ConnectionFilter)MemberwiseClone();
        c.States = new HashSet<TcpState>(States);
        c.BrowserNames = new HashSet<string>(BrowserNames, StringComparer.OrdinalIgnoreCase);
        return c;
    }

    public void Reset()
    {
        var d = new ConnectionFilter();
        Search = d.Search; ProcessName = d.ProcessName; Pid = d.Pid; ExecutablePath = d.ExecutablePath; RemoteIp = d.RemoteIp;
        LocalIp = d.LocalIp; RemotePort = d.RemotePort; LocalPort = d.LocalPort; Tcp = d.Tcp; Udp = d.Udp; IPv4 = d.IPv4; IPv6 = d.IPv6;
        States = new(); OnlyListening = false; OnlyEstablished = false; OnlyNewlyDetected = false; OnlyUnknownApplications = false;
        ObservedFromUtc = null; ObservedToUtc = null; BrowserNames = new(StringComparer.OrdinalIgnoreCase);
    }

    public bool Matches(TrackedConnection t, DateTime? nowUtc = null)
    {
        var c = t.Connection;
        var p = c.Process;

        if (c.Protocol == Protocol.Tcp ? !Tcp : !Udp) return false;
        var v6 = c.Record.IsIPv6;
        if (v6 ? !IPv6 : !IPv4) return false;
        if (States.Count > 0 && !States.Contains(c.State)) return false;
        if (OnlyListening && !c.IsListening) return false;
        if (OnlyEstablished && c.State != TcpState.Established) return false;
        if (OnlyNewlyDetected && !(t.DetectedAfterBaseline && (nowUtc ?? DateTime.UtcNow) - t.FirstObservedUtc < TimeSpan.FromMinutes(5))) return false;
        if (OnlyUnknownApplications && !(p.Path is null || p.Signature is SignatureStatus.NoEmbeddedSignature or SignatureStatus.Invalid)) return false;
        if (ProcessName is { Length: > 0 } pn && !Has(p.Name, pn)) return false;
        if (Pid is { } pid && p.Pid != pid) return false;
        if (ExecutablePath is { Length: > 0 } ep && !Has(p.Path, ep)) return false;
        if (RemoteIp is { Length: > 0 } rip && !Has(c.RemoteAddress, rip)) return false;
        if (LocalIp is { Length: > 0 } lip && !Has(c.LocalAddress, lip)) return false;
        if (RemotePort is { } rp && c.RemotePort != rp) return false;
        if (LocalPort is { } lp && c.LocalPort != lp) return false;
        if (ObservedFromUtc is { } f && t.LastObservedUtc < f) return false;
        if (ObservedToUtc is { } to && t.FirstObservedUtc > to) return false;
        if (BrowserNames.Count > 0 && !BrowserNames.Contains(p.Name)) return false;

        if (!string.IsNullOrWhiteSpace(Search))
        {
            foreach (var token in Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (!MatchesToken(t, token)) return false;
        }
        return true;
    }

    private static bool MatchesToken(TrackedConnection t, string token)
    {
        var c = t.Connection;
        return Has(c.Process.Name, token) || Has(c.Process.Path, token)
            || c.Process.Pid.ToString() == token
            || Has(c.LocalAddress, token) || Has(c.RemoteAddress, token)
            || c.LocalPort.ToString() == token || (c.RemoteAddress is not null && c.RemotePort.ToString() == token)
            || Has(c.State.ToDisplay(), token) || Has(c.Protocol.ToString(), token);
    }

    private static bool Has(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}

public static class FilterPresets
{
    public static readonly string[] BrowserProcesses =
        { "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "iexplore.exe", "arc.exe" };

    public static IReadOnlyDictionary<string, Func<ConnectionFilter>> BuiltIn { get; } = new Dictionary<string, Func<ConnectionFilter>>
    {
        ["All connections"] = () => new ConnectionFilter(),
        ["Established TCP"] = () => new ConnectionFilter { Udp = false, OnlyEstablished = true },
        ["Listening ports"] = () => new ConnectionFilter { OnlyListening = true },
        ["Browser connections"] = () => new ConnectionFilter { BrowserNames = new HashSet<string>(BrowserProcesses, StringComparer.OrdinalIgnoreCase) },
        ["Unknown applications"] = () => new ConnectionFilter { OnlyUnknownApplications = true },
        ["IPv6 connections"] = () => new ConnectionFilter { IPv4 = false },
        ["Newly detected endpoints"] = () => new ConnectionFilter { OnlyNewlyDetected = true },
    };
}
