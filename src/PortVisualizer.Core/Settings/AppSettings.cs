using System.Globalization;
using System.Reflection;

namespace PortVisualizer.Core.Settings;

public enum GraphLayoutMode { ForceDirected, Hierarchical, Circular, GroupedByProcess }

public interface ISettingsStore
{
    IReadOnlyDictionary<string, string> LoadAll();
    void Save(IEnumerable<KeyValuePair<string, string>> values);
}

/// <summary>User settings. Persisted as key/value rows in the SQLite Settings table.</summary>
public sealed class AppSettings
{
    // Monitoring
    public int RefreshIntervalMs { get; set; } = 1000;
    public bool EnableTcp { get; set; } = true;
    public bool EnableUdp { get; set; } = true;
    public bool EnableIPv4 { get; set; } = true;
    public bool EnableIPv6 { get; set; } = true;
    public bool MonitorListeningPorts { get; set; } = true;
    public bool RecordHistory { get; set; } = true;

    // Interface
    public bool DarkTheme { get; set; } = true;
    public string AccentColor { get; set; } = "#00E5A0";
    public bool GraphAnimation { get; set; } = true;
    public GraphLayoutMode GraphLayout { get; set; } = GraphLayoutMode.ForceDirected;
    public string VisibleColumns { get; set; } = "Status,Process,PID,Protocol,LocalAddress,LocalPort,RemoteAddress,RemotePort,State,FirstObserved,LastObserved";
    public bool MinimizeToTray { get; set; } = true;
    public string SavedFilterPresets { get; set; } = "";

    // Alerts
    public bool NotificationsEnabled { get; set; } = true;

    // Database
    public int RetentionDays { get; set; } = 30; // 0 = unlimited

    // Privacy
    public bool LocalOnlyMode { get; set; } = true;
    public bool DisableReverseDns { get; set; } = true;
    public bool DisableProcessPathRecording { get; set; }

    public bool ReverseDnsAllowed => !LocalOnlyMode && !DisableReverseDns;

    public static readonly int[] RefreshChoicesMs = { 500, 1000, 2000, 5000, 10000 };
    public static readonly int[] RetentionChoices = { 1, 7, 30, 90, 0 };

    private static readonly PropertyInfo[] Props = typeof(AppSettings)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).ToArray();

    public Dictionary<string, string> ToDictionary() =>
        Props.ToDictionary(p => p.Name, p => Convert.ToString(p.GetValue(this), CultureInfo.InvariantCulture) ?? "");

    public static AppSettings From(IReadOnlyDictionary<string, string> values)
    {
        var s = new AppSettings();
        foreach (var p in Props)
        {
            if (!values.TryGetValue(p.Name, out var raw)) continue;
            try
            {
                object? v = p.PropertyType switch
                {
                    var t when t == typeof(bool) => bool.Parse(raw),
                    var t when t == typeof(int) => int.Parse(raw, CultureInfo.InvariantCulture),
                    var t when t == typeof(string) => raw,
                    var t when t.IsEnum => Enum.Parse(t, raw),
                    _ => null,
                };
                if (v is not null) p.SetValue(s, v);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { /* keep default */ }
        }
        s.RefreshIntervalMs = Math.Clamp(s.RefreshIntervalMs, 250, 3_600_000);
        return s;
    }

    public AppSettings Clone() => From(ToDictionary());
}
