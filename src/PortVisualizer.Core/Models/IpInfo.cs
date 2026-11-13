using System.Net;
using System.Net.Sockets;

namespace PortVisualizer.Core.Models;

public enum IpCategory { Public, Private, Loopback, LinkLocal, Reserved, Unspecified }

public sealed record IpInfo(string Address, string Version, IpCategory Category)
{
    public string CategoryDisplay => Category switch
    {
        IpCategory.Public => "Public",
        IpCategory.Private => "Private",
        IpCategory.Loopback => "Loopback",
        IpCategory.LinkLocal => "Link-local",
        IpCategory.Reserved => "Reserved",
        _ => "Unspecified",
    };
}

/// <summary>Offline classification only: no lookups, no geolocation, no ownership claims.</summary>
public static class IpClassifier
{
    public static IpInfo Describe(IPAddress ip) =>
        new(ip.ToString(), ip.AddressFamily == AddressFamily.InterNetworkV6 && !ip.IsIPv4MappedToIPv6 ? "IPv6" : "IPv4", Classify(ip));

    public static IpCategory Classify(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return IpCategory.Loopback;
        var b = ip.GetAddressBytes();

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b.All(x => x == 0)) return IpCategory.Unspecified;
            if (b[0] == 10) return IpCategory.Private;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return IpCategory.Private;
            if (b[0] == 192 && b[1] == 168) return IpCategory.Private;
            if (b[0] == 169 && b[1] == 254) return IpCategory.LinkLocal;
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return IpCategory.Reserved;     // CGNAT 100.64/10
            if (b[0] == 0 || b[0] >= 224) return IpCategory.Reserved;                      // this-net, multicast, class E, broadcast
            if (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) return IpCategory.Reserved;     // 192.0.0/24, TEST-NET-1
            if (b[0] == 198 && b[1] is 18 or 19) return IpCategory.Reserved;               // benchmarking
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) return IpCategory.Reserved;      // TEST-NET-2
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) return IpCategory.Reserved;       // TEST-NET-3
            return IpCategory.Public;
        }

        if (b.All(x => x == 0)) return IpCategory.Unspecified;
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return IpCategory.LinkLocal;           // fe80::/10
        if ((b[0] & 0xfe) == 0xfc) return IpCategory.Private;                              // fc00::/7 unique local
        if (b[0] == 0xff) return IpCategory.Reserved;                                      // multicast
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return IpCategory.Reserved; // documentation
        if (b[0] == 0xfe && (b[1] & 0xc0) == 0xc0) return IpCategory.Reserved;            // deprecated site-local
        return IpCategory.Public;
    }

    public static bool TryParse(string? text, out IPAddress ip)
    {
        ip = IPAddress.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().Trim('[', ']');
        var pct = t.IndexOf('%');
        if (pct > 0) t = t[..pct];
        return IPAddress.TryParse(t, out ip!);
    }
}

/// <summary>Optional reverse DNS. Callers must only invoke this when the user has enabled it.</summary>
public sealed class ReverseDnsResolver
{
    private readonly Dictionary<string, string?> _cache = new();

    public async Task<string?> ResolveAsync(string ip, CancellationToken ct = default)
    {
        lock (_cache) { if (_cache.TryGetValue(ip, out var hit)) return hit; }
        string? name = null;
        try
        {
            var task = Dns.GetHostEntryAsync(ip, ct);
            name = (await task.WaitAsync(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false)).HostName;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException) { }
        lock (_cache) _cache[ip] = name;
        return name;
    }
}
