using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PortVisualizer.App.Services;
using PortVisualizer.Core.Alerts;
using PortVisualizer.Core.Export;
using PortVisualizer.Core.Models;
using PortVisualizer.Core.Settings;
using PortVisualizer.Data;

namespace PortVisualizer.App.ViewModels;

public sealed record HistoryRow(ConnectionEvent Event)
{
    public string Time => Event.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Type => Event.TypeDisplay;
    public string Process => Event.ProcessName;
    public int Pid => Event.Pid;
    public string Protocol => Event.Protocol.ToString().ToUpperInvariant();
    public string Local => Event.Local;
    public string Remote => Event.Remote ?? "";
    public string State => Event.StateDisplay;
}

public sealed partial class HistoryViewModel : PageViewModel
{
    private readonly HistoryRepository _repo;
    private readonly ExportService _export;
    private readonly SettingsService _settings;
    private int _offset;
    private const int PageSize = 500;

    public static readonly string[] RangeChoices = { "Last hour", "Last 24 hours", "Last 7 days", "Last 30 days", "All time", "Custom range" };
    public static readonly string[] TypeChoices =
    {
        "All events", "Connections opened", "Connections closed", "State changes", "Listening ports", "UDP endpoints", "New remote IPs", "New processes",
    };

    public HistoryViewModel(HistoryRepository repo, ExportService export, SettingsService settings)
    {
        _repo = repo; _export = export; _settings = settings;
        _rangeChoice = RangeChoices[1];
        _typeChoice = TypeChoices[0];
    }

    public ObservableCollection<HistoryRow> Rows { get; } = new();
    public ObservableCollection<TimelineBucket> Timeline { get; } = new();

    [ObservableProperty] private string _rangeChoice;
    [ObservableProperty] private string _fromText = DateTime.Now.AddDays(-1).ToString("yyyy-MM-dd HH:mm");
    [ObservableProperty] private string _toText = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
    [ObservableProperty] private string _processText = "";
    [ObservableProperty] private string _ipText = "";
    [ObservableProperty] private string _portText = "";
    [ObservableProperty] private string _typeChoice;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _canLoadMore;
    [ObservableProperty] private HistoryRow? _selected;
    [ObservableProperty] private string _detailText = "Select an event to see its details.";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _timelineCaption = "";

    public bool IsCustom => RangeChoice == "Custom range";
    partial void OnRangeChoiceChanged(string value) => OnPropertyChanged(nameof(IsCustom));

    partial void OnSelectedChanged(HistoryRow? value)
    {
        if (value is null) { DetailText = "Select an event to see its details."; return; }
        var e = value.Event;
        DetailText = string.Join(Environment.NewLine, new[]
        {
            $"Event ID: {e.Id}", $"Time (UTC): {ExportBuilder.Iso(e.TimestampUtc)}", $"Type: {e.TypeDisplay}",
            $"Process: {e.ProcessName} (PID {e.Pid})", $"Path: {e.ProcessPath ?? "not recorded"}",
            $"Protocol: {e.Protocol.ToString().ToUpperInvariant()}", $"Local: {e.Local}", $"Remote: {e.Remote ?? "n/a"}",
            $"State: {e.StateDisplay}",
        });
    }

    protected override void OnActivated() { if (Rows.Count == 0) _ = Search(); }

    private (DateTime? from, DateTime? to) Range()
    {
        var now = DateTime.UtcNow;
        return RangeChoice switch
        {
            "Last hour" => (now.AddHours(-1), null),
            "Last 24 hours" => (now.AddDays(-1), null),
            "Last 7 days" => (now.AddDays(-7), null),
            "Last 30 days" => (now.AddDays(-30), null),
            "Custom range" => (Parse(FromText), Parse(ToText)),
            _ => (null, null),
        };
    }

    private static DateTime? Parse(string s) =>
        DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    private HistoryQuery BuildQuery()
    {
        var (from, to) = Range();
        var q = new HistoryQuery { FromUtc = from, ToUtc = to, Process = ProcessText, Ip = IpText, Limit = PageSize, Offset = _offset };
        if (int.TryParse(PortText, out var p)) q.Port = p;
        q.Types = TypeChoice switch
        {
            "Connections opened" => new() { ChangeType.NewConnection },
            "Connections closed" => new() { ChangeType.ConnectionClosed },
            "State changes" => new() { ChangeType.StateChanged },
            "Listening ports" => new() { ChangeType.NewListeningPort, ChangeType.ListeningPortClosed },
            "UDP endpoints" => new() { ChangeType.NewUdpEndpoint, ChangeType.UdpEndpointRemoved },
            "New remote IPs" => new() { ChangeType.NewRemoteIp },
            "New processes" => new() { ChangeType.NewProcess },
            _ => new(),
        };
        return q;
    }

    [RelayCommand]
    private async Task Search()
    {
        Error = "";
        if (IsCustom && (Parse(FromText) is null || Parse(ToText) is null)) { Error = "Enter dates as yyyy-MM-dd HH:mm (local time)."; return; }
        if (!string.IsNullOrWhiteSpace(PortText) && !int.TryParse(PortText, out _)) { Error = "Port must be a number."; return; }
        _offset = 0;
        Rows.Clear();
        await LoadPageAsync();
        await LoadTimelineAsync();
    }

    [RelayCommand]
    private async Task LoadMore() { _offset += PageSize; await LoadPageAsync(); }

    [RelayCommand]
    private async Task ResetFilters()
    {
        RangeChoice = RangeChoices[1]; ProcessText = IpText = PortText = ""; TypeChoice = TypeChoices[0];
        await Search();
    }

    private async Task LoadPageAsync()
    {
        Busy = true;
        try
        {
            var q = BuildQuery();
            var (page, total) = await Task.Run(() => (_repo.Query(q), _repo.Count(q)));
            foreach (var e in page) Rows.Add(new HistoryRow(e));
            CanLoadMore = Rows.Count < total;
            Summary = $"{Rows.Count:N0} of {total:N0} events" + (_settings.Current.RecordHistory ? "" : "  (history recording is currently OFF)");
        }
        catch (Exception ex) { Error = "Query failed: " + ex.Message; }
        finally { Busy = false; }
    }

    private async Task LoadTimelineAsync()
    {
        try
        {
            var (from, to) = Range();
            var end = to ?? DateTime.UtcNow;
            var start = from ?? end.AddDays(-7);
            var bucket = Math.Max(1, (int)Math.Ceiling((end - start).TotalMinutes / 60));
            var data = await Task.Run(() => _repo.Timeline(start, end, bucket));
            Timeline.Clear();
            foreach (var b in data) Timeline.Add(b);
            TimelineCaption = data.Count == 0 ? "No events in range" : $"Event timeline ({bucket} min buckets): green = opened, grey = closed";
        }
        catch (Exception ex) { Error = "Timeline failed: " + ex.Message; }
    }

    [RelayCommand]
    private async Task Export()
    {
        var q = BuildQuery(); q.Offset = 0; q.Limit = 100000;
        var (from, to) = Range();
        var events = await Task.Run(() => _repo.Query(q));
        await _export.ExportAsync(ExportBuilder.History(events, from, to), Application.Current.MainWindow, "history");
    }

    [RelayCommand]
    private void CopyDetails() => Shell.Copy(DetailText);
}

public sealed partial class RuleItem : ObservableObject
{
    private readonly Action<RuleItem> _changed;
    private bool _loading = true;
    public RuleItem(AlertRule r, Action<RuleItem> changed)
    {
        Id = r.Id; _name = r.Name; Type = r.Type; _value = r.Value; _isEnabled = r.IsEnabled; _severity = r.Severity; _changed = changed; _loading = false;
    }
    public long Id { get; }
    public AlertRuleType Type { get; }
    public string TypeDisplay => AlertsViewModel.Describe(Type);
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _value;
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private AlertSeverity _severity;
    partial void OnIsEnabledChanged(bool value) { if (!_loading) _changed(this); }
    partial void OnSeverityChanged(AlertSeverity value) { if (!_loading) _changed(this); }
    public AlertRule ToRule() => new(Id, Name, Type, Value, IsEnabled, Severity);
}

public sealed partial class AlertsViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private readonly SettingsService _settings;

    public AlertsViewModel(MonitoringService monitoring, SettingsService settings)
    {
        _monitoring = monitoring; _settings = settings;
        LoadRules();
    }

    public ObservableCollection<Alert> Alerts => _monitoring.Alerts;
    public ObservableCollection<RuleItem> Rules { get; } = new();
    public IReadOnlyList<AlertRuleType> RuleTypes { get; } = Enum.GetValues<AlertRuleType>();
    public IReadOnlyList<AlertSeverity> Severities { get; } = Enum.GetValues<AlertSeverity>();

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private AlertRuleType _newType = AlertRuleType.BlockedIp;
    [ObservableProperty] private string _newValue = "";
    [ObservableProperty] private AlertSeverity _newSeverity = AlertSeverity.Warning;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private RuleItem? _selectedRule;

    public bool NotificationsEnabled
    {
        get => _settings.Current.NotificationsEnabled;
        set { _settings.Update(s => s.NotificationsEnabled = value); OnPropertyChanged(); }
    }

    public string Hint => NewType switch
    {
        AlertRuleType.BlockedIp => "IP addresses or CIDR blocks, comma separated. Example: 203.0.113.0/24, 2001:db8::/32",
        AlertRuleType.BlockedPort => "Remote ports or ranges. Example: 23, 6660-6669",
        AlertRuleType.MonitoredPort => "Local or remote ports to watch. Example: 3389, 5900",
        AlertRuleType.WhitelistProcess => "Process names (or full paths) that should never raise alerts. Example: chrome.exe",
        AlertRuleType.WhitelistIp => "Remote IPs or CIDR blocks that should never raise alerts.",
        AlertRuleType.UnexpectedState => "TCP states to flag. Example: CLOSE_WAIT, LAST_ACK",
        _ => "No value needed. Fires when the event is first observed.",
    };

    public bool NeedsValue => NewType is not (AlertRuleType.NewListeningPort or AlertRuleType.NewRemoteIp or AlertRuleType.NewProcess);

    partial void OnNewTypeChanged(AlertRuleType value) { OnPropertyChanged(nameof(Hint)); OnPropertyChanged(nameof(NeedsValue)); }

    public static string Describe(AlertRuleType t) => t switch
    {
        AlertRuleType.NewListeningPort => "New listening port",
        AlertRuleType.NewRemoteIp => "New remote IP",
        AlertRuleType.NewProcess => "New network process",
        AlertRuleType.UnexpectedState => "Unexpected TCP state",
        AlertRuleType.BlockedIp => "Listed IP",
        AlertRuleType.BlockedPort => "Listed port",
        AlertRuleType.MonitoredPort => "Monitored port",
        AlertRuleType.WhitelistProcess => "Whitelist: process",
        AlertRuleType.WhitelistIp => "Whitelist: IP",
        _ => t.ToString(),
    };

    private void LoadRules()
    {
        _monitoring.ReloadRules();
        Rules.Clear();
        foreach (var r in _monitoring.Rules.GetAll()) Rules.Add(new RuleItem(r, Save));
    }

    private void Save(RuleItem item)
    {
        _monitoring.Rules.Save(item.ToRule());
        _monitoring.ReloadRules();
    }

    [RelayCommand]
    private void AddRule()
    {
        Error = "";
        var value = NewValue.Trim();
        if (NeedsValue && value.Length == 0) { Error = "This rule type needs a value."; return; }
        if (NewType is AlertRuleType.BlockedPort or AlertRuleType.MonitoredPort && !PortMatcher.IsValid(value)) { Error = "Invalid port list. Use numbers 0-65535 and ranges like 8000-8100."; return; }
        if (NewType is AlertRuleType.BlockedIp or AlertRuleType.WhitelistIp)
        {
            var bad = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(p => !IpClassifier.TryParse(p.Split('/')[0], out _) || (p.Contains('/') && !int.TryParse(p.Split('/')[1], out _)));
            if (bad is not null) { Error = $"'{bad}' is not a valid IP address or CIDR block."; return; }
        }
        var name = string.IsNullOrWhiteSpace(NewName) ? $"{Describe(NewType)}: {value}".TrimEnd(' ', ':') : NewName.Trim();
        var id = _monitoring.Rules.Save(new AlertRule(0, name, NewType, value, true, NewSeverity));
        Rules.Add(new RuleItem(new AlertRule(id, name, NewType, value, true, NewSeverity), Save));
        _monitoring.ReloadRules();
        NewName = ""; NewValue = "";
    }

    [RelayCommand]
    private void DeleteRule()
    {
        if (SelectedRule is not { } r) return;
        _monitoring.Rules.Delete(r.Id);
        Rules.Remove(r);
        _monitoring.ReloadRules();
    }

    [RelayCommand] private void ClearAlerts() => _monitoring.Alerts.Clear();
}

public sealed record ChoiceItem(string Label, int Value) { public override string ToString() => Label; }
public sealed record SwatchItem(string Name, string Hex);

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsService _svc;
    private readonly PortVisualizerDatabase _db;
    private readonly HistoryRepository _history;
    private readonly Bootstrap _bootstrap;
    private readonly ExportService _export;
    private readonly MonitoringService _monitoring;

    public SettingsViewModel(SettingsService svc, PortVisualizerDatabase db, HistoryRepository history, Bootstrap bootstrap,
        ExportService export, MonitoringService monitoring, LiveConnectionsViewModel live)
    {
        _svc = svc; _db = db; _history = history; _bootstrap = bootstrap; _export = export; _monitoring = monitoring;
        Columns = live.Columns;
        var ms = svc.Current.RefreshIntervalMs;
        _refreshChoice = IntervalChoices.FirstOrDefault(c => c.Value == ms) ?? IntervalChoices[^1];
        _customInterval = ms.ToString();
        _retentionChoice = RetentionChoices.First(c => c.Value == svc.Current.RetentionDays);
        IsAdmin = Shell.IsElevated();
    }

    public IReadOnlyList<ChoiceItem> IntervalChoices { get; } = new[]
    {
        new ChoiceItem("500 milliseconds", 500), new ChoiceItem("1 second (default)", 1000), new ChoiceItem("2 seconds", 2000),
        new ChoiceItem("5 seconds", 5000), new ChoiceItem("10 seconds", 10000), new ChoiceItem("Custom...", -1),
    };
    public IReadOnlyList<ChoiceItem> RetentionChoices { get; } = new[]
    {
        new ChoiceItem("1 day", 1), new ChoiceItem("7 days", 7), new ChoiceItem("30 days", 30), new ChoiceItem("90 days", 90), new ChoiceItem("Unlimited", 0),
    };
    public IReadOnlyList<SwatchItem> Swatches { get; } = new[]
    {
        new SwatchItem("Green", "#00E5A0"), new SwatchItem("Cyan", "#00D4FF"), new SwatchItem("Blue", "#3B82F6"),
        new SwatchItem("Purple", "#A855F7"), new SwatchItem("Yellow", "#FACC15"), new SwatchItem("Orange", "#FB923C"),
    };
    public IReadOnlyList<GraphLayoutMode> Layouts { get; } = Enum.GetValues<GraphLayoutMode>();
    public ObservableCollection<ColumnOption> Columns { get; }
    public string DatabasePath => _db.Path;
    public string LogPath => AppPaths.LogDirectory;
    public bool IsAdmin { get; }
    public string AdminText => IsAdmin ? "Running with administrator rights." : "Running without administrator rights (recommended).";

    private AppSettings S => _svc.Current;
    private void Set(Action<AppSettings> a, string name) { _svc.Update(a); OnPropertyChanged(name); }

    public bool EnableTcp { get => S.EnableTcp; set => Set(s => s.EnableTcp = value, nameof(EnableTcp)); }
    public bool EnableUdp { get => S.EnableUdp; set => Set(s => s.EnableUdp = value, nameof(EnableUdp)); }
    public bool EnableIPv4 { get => S.EnableIPv4; set => Set(s => s.EnableIPv4 = value, nameof(EnableIPv4)); }
    public bool EnableIPv6 { get => S.EnableIPv6; set => Set(s => s.EnableIPv6 = value, nameof(EnableIPv6)); }
    public bool MonitorListening { get => S.MonitorListeningPorts; set => Set(s => s.MonitorListeningPorts = value, nameof(MonitorListening)); }
    public bool RecordHistory { get => S.RecordHistory; set => Set(s => s.RecordHistory = value, nameof(RecordHistory)); }
    public bool DarkTheme { get => S.DarkTheme; set { Set(s => s.DarkTheme = value, nameof(DarkTheme)); OnPropertyChanged(nameof(LightTheme)); } }
    public bool LightTheme { get => !S.DarkTheme; set { if (value) DarkTheme = false; else DarkTheme = true; } }
    public string AccentColor { get => S.AccentColor; set => Set(s => s.AccentColor = value, nameof(AccentColor)); }
    public bool GraphAnimation { get => S.GraphAnimation; set => Set(s => s.GraphAnimation = value, nameof(GraphAnimation)); }
    public GraphLayoutMode GraphLayout { get => S.GraphLayout; set => Set(s => s.GraphLayout = value, nameof(GraphLayout)); }
    public bool MinimizeToTray { get => S.MinimizeToTray; set => Set(s => s.MinimizeToTray = value, nameof(MinimizeToTray)); }
    public bool NotificationsEnabled { get => S.NotificationsEnabled; set => Set(s => s.NotificationsEnabled = value, nameof(NotificationsEnabled)); }
    public bool LocalOnlyMode
    {
        get => S.LocalOnlyMode;
        set { Set(s => s.LocalOnlyMode = value, nameof(LocalOnlyMode)); OnPropertyChanged(nameof(ReverseDnsHint)); }
    }
    public bool DisableReverseDns
    {
        get => S.DisableReverseDns;
        set { Set(s => s.DisableReverseDns = value, nameof(DisableReverseDns)); OnPropertyChanged(nameof(ReverseDnsHint)); }
    }
    public bool DisablePathRecording { get => S.DisableProcessPathRecording; set => Set(s => s.DisableProcessPathRecording = value, nameof(DisablePathRecording)); }
    public string ReverseDnsHint => S.ReverseDnsAllowed
        ? "Reverse DNS lookups are enabled: selecting a remote node lets you resolve its hostname (this contacts your DNS server)."
        : "Reverse DNS is off. No hostname lookups are made. To use it, turn off Local-only mode and Disable reverse DNS.";

    [ObservableProperty] private ChoiceItem _refreshChoice;
    [ObservableProperty] private string _customInterval;
    [ObservableProperty] private ChoiceItem _retentionChoice;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _customAccent = "";

    public bool IsCustomInterval => RefreshChoice.Value < 0;

    partial void OnRefreshChoiceChanged(ChoiceItem value)
    {
        OnPropertyChanged(nameof(IsCustomInterval));
        if (value.Value > 0) { _svc.Update(s => s.RefreshIntervalMs = value.Value); CustomInterval = value.Value.ToString(); }
    }

    partial void OnRetentionChoiceChanged(ChoiceItem value) => _svc.Update(s => s.RetentionDays = value.Value);

    [RelayCommand]
    private void ApplyCustomInterval()
    {
        if (int.TryParse(CustomInterval, out var ms) && ms >= 250 && ms <= 3_600_000)
        { _svc.Update(s => s.RefreshIntervalMs = ms); Message = $"Refresh interval set to {ms} ms."; }
        else Message = "Enter a whole number of milliseconds between 250 and 3600000.";
    }

    [RelayCommand] private void PickAccent(string hex) => AccentColor = hex;

    [RelayCommand]
    private void ApplyCustomAccent()
    {
        try { _ = System.Windows.Media.ColorConverter.ConvertFromString(CustomAccent); AccentColor = CustomAccent; }
        catch (FormatException) { Message = "Use a colour such as #00E5A0."; }
    }

    [RelayCommand]
    private void ClearHistory()
    {
        if (MessageBox.Show("Delete all stored connection history? This cannot be undone.", "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _history.ClearHistory();
        Message = "Connection history cleared.";
    }

    [RelayCommand]
    private async Task ExportHistory()
    {
        var events = await Task.Run(() => _history.Query(new HistoryQuery { Limit = 100000 }));
        await _export.ExportAsync(ExportBuilder.History(events, null, null), Application.Current.MainWindow, "history-all");
    }

    [RelayCommand]
    private void BackupDatabase()
    {
        var dlg = new SaveFileDialog { Filter = "SQLite database (*.db)|*.db", FileName = $"portvisualizer-backup-{DateTime.Now:yyyyMMdd-HHmm}.db" };
        if (dlg.ShowDialog() != true) return;
        try { _history.GetType(); _db.Backup(dlg.FileName); Message = "Backup written to " + dlg.FileName; }
        catch (Exception ex) { Message = "Backup failed: " + ex.Message; }
    }

    [RelayCommand]
    private void ChangeDatabaseLocation()
    {
        var dlg = new OpenFolderDialog { Title = "Choose a folder for the Port Visualizer database" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var target = Path.Combine(dlg.FolderName, "portvisualizer.db");
            if (File.Exists(target) && MessageBox.Show("A database already exists there. Use it instead of copying the current one?", "Database location", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            if (!File.Exists(target)) _db.Backup(target);
            _bootstrap.DatabasePath = target;
            _bootstrap.Save();
            Message = $"Database location changed. Restart Port Visualizer to use {target}. The previous file was left in place.";
        }
        catch (Exception ex) { Message = "Could not change location: " + ex.Message; }
    }

    [RelayCommand]
    private void RestartAsAdmin()
    {
        if (MessageBox.Show("Administrator mode lets Port Visualizer read the executable path of some protected processes. It does not change what is monitored and never terminates anything.\n\nRestart with administrator rights?",
                "Restart as administrator", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            Shell.RestartElevated();
    }

    [RelayCommand] private void OpenLogs() { Directory.CreateDirectory(AppPaths.LogDirectory); System.Diagnostics.Process.Start("explorer.exe", AppPaths.LogDirectory); }
    [RelayCommand] private void OpenDataFolder() => System.Diagnostics.Process.Start("explorer.exe", Path.GetDirectoryName(_db.Path)!);
}

public sealed class AboutViewModel : PageViewModel
{
    public string Version { get; } = "Version " + (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "1.0.0");
    public string Tagline => "See what your computer is communicating with.";
    public string License { get; } = ReadLicense();
    public string DataLocation => AppPaths.DataDirectory;
    public string Statement =>
        "Port Visualizer is a defensive, local network visibility tool. It reads the Windows connection tables and process list. " +
        "It never captures packets or payloads, never collects credentials, never terminates processes or changes the firewall, and never uploads data. " +
        "An unfamiliar connection is not evidence of malware, and this app does not classify connections as malicious.";

    private static string ReadLicense()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LICENSE.txt");
            return s is null ? "MIT License" : new StreamReader(s).ReadToEnd();
        }
        catch { return "MIT License"; }
    }
}
