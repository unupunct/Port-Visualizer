using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PortVisualizer.Core.Alerts;
using PortVisualizer.Core.Export;
using PortVisualizer.Core.Models;
using PortVisualizer.Core.Monitoring;
using PortVisualizer.Core.Native;
using PortVisualizer.Core.Processes;
using PortVisualizer.Core.Settings;
using PortVisualizer.Data;

namespace PortVisualizer.App.Services;

/// <summary>Tiny bootstrap file (the only thing outside the database) that remembers a custom database location.</summary>
public sealed class Bootstrap
{
    public string? DatabasePath { get; set; }

    public static Bootstrap Load()
    {
        try
        {
            if (File.Exists(AppPaths.BootstrapFile))
                return JsonSerializer.Deserialize<Bootstrap>(File.ReadAllText(AppPaths.BootstrapFile)) ?? new Bootstrap();
        }
        catch { }
        return new Bootstrap();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(AppPaths.BootstrapFile, JsonSerializer.Serialize(this));
    }
}

public sealed class SettingsService
{
    private readonly ISettingsStore _store;
    public SettingsService(ISettingsStore store)
    {
        _store = store;
        Current = AppSettings.From(store.LoadAll());
    }

    public AppSettings Current { get; private set; }
    public event Action<AppSettings>? Changed;

    public void Update(Action<AppSettings> change)
    {
        var next = Current.Clone();
        change(next);
        Current = next;
        _store.Save(next.ToDictionary());
        Changed?.Invoke(next);
    }
}

public sealed record ActivitySample(DateTime TimeUtc, int New, int Closed, int Active);

public sealed class FeedItem
{
    public FeedItem(ConnectionEvent e) => Event = e;
    public ConnectionEvent Event { get; }
    public string Time => Event.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
    public string Title => Event.TypeDisplay;
    public string Process => Event.ProcessName;
    public string Endpoints => Event.Endpoints;
    public string State => Event.StateDisplay;
    public string Accent => Event.Type switch
    {
        ChangeType.NewConnection or ChangeType.NewUdpEndpoint => "#00E5A0",
        ChangeType.ConnectionClosed or ChangeType.UdpEndpointRemoved or ChangeType.ListeningPortClosed => "#8793A0",
        ChangeType.NewListeningPort => "#A855F7",
        ChangeType.StateChanged => "#00D4FF",
        _ => "#FACC15",
    };
}

/// <summary>
/// Owns the monitor, history writer and alert engine, and marshals results to the UI thread without ever
/// letting a slow UI back up the polling loop (results are coalesced; events are never dropped).
/// </summary>
public sealed class MonitoringService : IDisposable
{
    private readonly System.Windows.Threading.Dispatcher _ui;
    private readonly ILogger<MonitoringService> _log;
    private readonly object _lock = new();
    private readonly List<TrackedChange> _pendingChanges = new();
    private readonly List<Alert> _pendingAlerts = new();
    private MonitorCycleResult? _pendingResult;
    private bool _scheduled;
    private readonly Dictionary<string, DateTime> _warningIps = new();
    private readonly System.Threading.Timer _maintenance;
    private DateTime _todayStartUtc;

    public MonitoringService(NetworkMonitor monitor, ProcessResolver resolver, HistoryWriter writer, HistoryRepository history,
        AlertRuleRepository rules, SettingsService settings, ProcessEnricher enricher, ILogger<MonitoringService> log)
    {
        Monitor = monitor; Resolver = resolver; Writer = writer; History = history; Rules = rules; Settings = settings; Enricher = enricher; _log = log;
        _ui = System.Windows.Application.Current.Dispatcher;
        AlertEngine = new AlertEngine();
        ReloadRules();
        ApplySettings(settings.Current);
        settings.Changed += s => { ApplySettings(s); };
        monitor.CycleCompleted += OnCycle;
        monitor.ErrorOccurred += msg => _ui.BeginInvoke(() => { LastError = msg; ErrorChanged?.Invoke(); });
        monitor.StateChanged += _ => _ui.BeginInvoke(() => StateChanged?.Invoke());
        writer.RecordHistory = () => Settings.Current.RecordHistory;
        writer.RecordPaths = () => !Settings.Current.DisableProcessPathRecording;
        _maintenance = new System.Threading.Timer(_ => RunMaintenance(), null, TimeSpan.FromSeconds(5), TimeSpan.FromHours(1));
    }

    public NetworkMonitor Monitor { get; }
    public ProcessResolver Resolver { get; }
    public HistoryWriter Writer { get; }
    public HistoryRepository History { get; }
    public AlertRuleRepository Rules { get; }
    public SettingsService Settings { get; }
    public ProcessEnricher Enricher { get; }
    public AlertEngine AlertEngine { get; }

    public MonitorCycleResult? Latest { get; private set; }
    public string? LastError { get; private set; }
    public bool FeedPaused { get; set; }
    public long NewConnectionsToday { get; private set; }
    public ObservableCollection<FeedItem> Feed { get; } = new();
    public ObservableCollection<Alert> Alerts { get; } = new();
    public List<ActivitySample> Activity { get; } = new();

    public event Action<MonitorCycleResult>? SnapshotUpdated;
    public event Action<IReadOnlyList<Alert>>? AlertsRaised;
    public event Action? StateChanged;
    public event Action? ErrorChanged;

    public IReadOnlySet<string> WarningIps
    {
        get
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-2);
            return _warningIps.Where(k => k.Value > cutoff).Select(k => k.Key).ToHashSet();
        }
    }

    public void Start()
    {
        try
        {
            History.ClearCurrentConnections();
            _todayStartUtc = DateTime.Today.ToUniversalTime();
            NewConnectionsToday = History.CountNewConnectionsSince(_todayStartUtc);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Startup history initialisation failed"); }
        Monitor.Start();
        _log.LogInformation("Monitoring started (interval {Interval} ms)", Monitor.Interval.TotalMilliseconds);
    }

    public void Pause() { Monitor.Pause(); _log.LogInformation("Monitoring paused by user"); }
    public void Resume() { Monitor.Resume(); _log.LogInformation("Monitoring resumed by user"); }

    public void ReloadRules()
    {
        try { Rules.SeedDefaultsIfEmpty(); AlertEngine.SetRules(Rules.GetAll()); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to load alert rules"); }
    }

    private void ApplySettings(AppSettings s)
    {
        Monitor.Interval = TimeSpan.FromMilliseconds(s.RefreshIntervalMs);
        Monitor.Options = new MonitorOptions(s.EnableTcp, s.EnableUdp, s.EnableIPv4, s.EnableIPv6, s.MonitorListeningPorts);
        Resolver.RecordPaths = !s.DisableProcessPathRecording;
    }

    private void RunMaintenance()
    {
        try
        {
            var removed = History.ApplyRetention(Settings.Current.RetentionDays, DateTime.UtcNow);
            if (removed > 0) _log.LogInformation("Retention cleanup removed {Count} events", removed);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Retention cleanup failed"); }
    }

    private void OnCycle(MonitorCycleResult result)
    {
        var changes = result.Tracker.Changes;
        try { Writer.Enqueue(changes); } catch (Exception ex) { _log.LogWarning(ex, "Enqueue failed"); }

        var alerts = new List<Alert>();
        foreach (var c in changes)
            foreach (var a in AlertEngine.Evaluate(c.Event)) alerts.Add(a);

        lock (_lock)
        {
            _pendingResult = result;
            _pendingChanges.AddRange(changes);
            _pendingAlerts.AddRange(alerts);
            if (_scheduled) return;
            _scheduled = true;
        }
        _ui.BeginInvoke(DrainOnUi, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void DrainOnUi()
    {
        MonitorCycleResult? result; List<TrackedChange> changes; List<Alert> alerts;
        lock (_lock)
        {
            result = _pendingResult; changes = new(_pendingChanges); alerts = new(_pendingAlerts);
            _pendingChanges.Clear(); _pendingAlerts.Clear(); _pendingResult = null; _scheduled = false;
        }
        if (result is null) return;

        if (DateTime.Today.ToUniversalTime() != _todayStartUtc) { _todayStartUtc = DateTime.Today.ToUniversalTime(); NewConnectionsToday = 0; }
        var opened = changes.Count(c => c.Event.Type is ChangeType.NewConnection or ChangeType.NewUdpEndpoint);
        var closed = changes.Count(c => c.Event.Type is ChangeType.ConnectionClosed or ChangeType.UdpEndpointRemoved);
        NewConnectionsToday += opened;

        Latest = result;
        LastError = result.Warnings.Count > 0 ? string.Join("; ", result.Warnings) : null;

        Activity.Add(new ActivitySample(result.TimestampUtc, opened, closed, result.Tracker.Active.Count));
        if (Activity.Count > 240) Activity.RemoveRange(0, Activity.Count - 240);

        if (!FeedPaused)
        {
            foreach (var c in changes) Feed.Insert(0, new FeedItem(c.Event));
            while (Feed.Count > 300) Feed.RemoveAt(Feed.Count - 1);
        }

        foreach (var a in alerts)
        {
            Alerts.Insert(0, a);
            if (a.Event.RemoteAddress is { } ip) _warningIps[ip] = DateTime.UtcNow;
        }
        while (Alerts.Count > 500) Alerts.RemoveAt(Alerts.Count - 1);
        if (_warningIps.Count > 2000) foreach (var k in _warningIps.Where(k => k.Value < DateTime.UtcNow.AddMinutes(-5)).Select(k => k.Key).ToList()) _warningIps.Remove(k);

        try { SnapshotUpdated?.Invoke(result); }
        catch (Exception ex) { _log.LogError(ex, "Snapshot handler failed"); }
        if (alerts.Count > 0) AlertsRaised?.Invoke(alerts);
    }

    public void Dispose()
    {
        _maintenance.Dispose();
    }
}

public static class Shell
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize; public uint fMask; public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow; public IntPtr hInstApp; public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon; public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void OpenLocation(ProcessInfo p)
    {
        if (p.Path is null || !File.Exists(p.Path))
        {
            MessageBox.Show("The executable path is not available (protected process, access denied, or the process has exited).",
                "Open location", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p.Path}\"") { UseShellExecute = true });
    }

    public static void FileProperties(ProcessInfo p)
    {
        if (p.Path is null || !File.Exists(p.Path)) { OpenLocation(p); return; }
        var info = new ShellExecuteInfo { cbSize = Marshal.SizeOf<ShellExecuteInfo>(), fMask = 0x0000000C, lpVerb = "properties", lpFile = p.Path, nShow = 5 };
        ShellExecuteEx(ref info);
    }

    public static void ApplyDarkTitleBar(Window w, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).EnsureHandle();
            var v = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int));
        }
        catch { }
    }

    public static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (COMException) { /* clipboard busy */ }
    }

    public static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public static void RestartElevated()
    {
        var exe = Environment.ProcessPath!;
        try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" }); System.Windows.Application.Current.Shutdown(); }
        catch (System.ComponentModel.Win32Exception) { /* user declined UAC */ }
    }
}

public static class ConnectionText
{
    public static string Details(TrackedConnection t)
    {
        var c = t.Connection;
        var lines = new List<string>
        {
            $"Process: {c.Process.Name} (PID {c.Process.Pid})",
            $"Path: {c.Process.Path ?? "unavailable"}",
            $"Protocol: {c.Protocol.ToString().ToUpperInvariant()}",
            $"Local: {c.LocalEndpoint}",
            $"Remote: {c.RemoteEndpoint ?? (c.IsUdp ? "n/a (UDP endpoint)" : "n/a")}",
            $"State: {c.State.ToDisplay()}",
            $"First observed (UTC): {ExportBuilder.Iso(t.FirstObservedUtc)}",
            $"Last observed (UTC): {ExportBuilder.Iso(t.LastObservedUtc)}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    public static string ProcessText(ProcessInfo p, int connections = 0) =>
        string.Join(Environment.NewLine, new[]
        {
            $"Name: {p.Name}", $"PID: {p.Pid}", $"Path: {p.Path ?? "unavailable"}",
            $"Started (UTC): {(p.StartTimeUtc is null ? "unavailable" : ExportBuilder.Iso(p.StartTimeUtc))}",
            $"Publisher: {p.Publisher ?? "unavailable"}", $"Signature: {p.SignatureDisplay}",
            connections > 0 ? $"Connections: {connections}" : "",
        }.Where(s => s.Length > 0));
}

public sealed class ExportService
{
    public async Task<bool> ExportAsync(ExportTable table, Window? owner, string suggestedName)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export " + table.Title,
            Filter = "CSV file (*.csv)|*.csv|JSON file (*.json)|*.json|HTML report (*.html)|*.html",
            FileName = $"{suggestedName}-{DateTime.Now:yyyyMMdd-HHmmss}",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(owner) != true) return false;
        var format = dlg.FilterIndex switch { 2 => ExportFormat.Json, 3 => ExportFormat.Html, _ => ExportFormat.Csv };
        try
        {
            await Exporter.WriteAsync(dlg.FileName, table, format);
            MessageBox.Show(owner, $"Exported {table.Rows.Count} rows to:\n{dlg.FileName}", "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(owner, "Export failed: " + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}

// ---- value converters ----

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c)
    {
        var b = value is true;
        if (parameter is "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c)
    {
        var has = value is not null && !(value is string s && s.Length == 0);
        if (parameter is "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object parameter, CultureInfo c)
    {
        try { var b = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(value?.ToString() ?? "#888888")); b.Freeze(); return b; }
        catch { return Brushes.Gray; }
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Maps a connection status word to a theme-independent accent brush.</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static SolidColorBrush B(string hex) { var b = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    private static readonly Dictionary<string, SolidColorBrush> Map = new()
    {
        ["NEW"] = B("#FACC15"), ["ACTIVE"] = B("#00E5A0"), ["CLOSED"] = B("#6B7280"), ["LISTENING"] = B("#A855F7"),
        ["INFO"] = B("#00D4FF"), ["WARNING"] = B("#FB923C"), ["CRITICAL"] = B("#EF4444"),
        ["Info"] = B("#00D4FF"), ["Warning"] = B("#FB923C"), ["Critical"] = B("#EF4444"),
    };
    public object Convert(object value, Type t, object parameter, CultureInfo c) =>
        Map.TryGetValue(value?.ToString() ?? "", out var b) ? b : Map["CLOSED"];
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
