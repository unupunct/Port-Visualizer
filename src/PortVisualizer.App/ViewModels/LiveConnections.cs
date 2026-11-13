using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortVisualizer.App.Services;
using PortVisualizer.App.Views;
using PortVisualizer.Core.Export;
using PortVisualizer.Core.Filtering;
using PortVisualizer.Core.Models;

namespace PortVisualizer.App.ViewModels;

public sealed partial class ConnectionRow : ObservableObject
{
    public ConnectionRow(TrackedConnection t, DateTime now) { Tracked = t; Key = t.Key; Update(t, now); }

    public TrackedConnection Tracked { get; private set; }
    public string Key { get; }
    public ProcessInfo Process => Tracked.Connection.Process;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _processName = "";
    [ObservableProperty] private int _pid;
    [ObservableProperty] private string _protocol = "";
    [ObservableProperty] private string _localAddress = "";
    [ObservableProperty] private int _localPort;
    [ObservableProperty] private string _remoteAddress = "";
    [ObservableProperty] private string _remotePort = "";
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private string _firstObserved = "";
    [ObservableProperty] private string _lastObserved = "";

    public void Update(TrackedConnection t, DateTime now)
    {
        Tracked = t;
        var c = t.Connection;
        Status = t.IsClosed ? "CLOSED" : c.IsListening ? "LISTENING" : t.DetectedAfterBaseline && now - t.FirstObservedUtc < TimeSpan.FromSeconds(10) ? "NEW" : "ACTIVE";
        ProcessName = c.Process.Name; Pid = c.Process.Pid;
        Protocol = c.Protocol.ToString().ToUpperInvariant();
        LocalAddress = c.LocalAddress; LocalPort = c.LocalPort;
        RemoteAddress = c.RemoteAddress ?? ""; RemotePort = c.RemoteAddress is null ? "" : c.RemotePort.ToString();
        State = c.State.ToDisplay();
        FirstObserved = t.FirstObservedUtc.ToLocalTime().ToString("HH:mm:ss");
        LastObserved = t.LastObservedUtc.ToLocalTime().ToString("HH:mm:ss");
    }
}

public sealed partial class ColumnOption : ObservableObject
{
    public ColumnOption(string key, string header, bool visible) { Key = key; Header = header; _isVisible = visible; }
    public string Key { get; }
    public string Header { get; }
    [ObservableProperty] private bool _isVisible;
}

public sealed record PresetSpec(string? Search, bool Tcp, bool Udp, bool IPv4, bool IPv6, bool Established, bool Listening, string State);

public sealed partial class LiveConnectionsViewModel : PageViewModel
{
    public const string AnyState = "Any state";
    private readonly MonitoringService _monitoring;
    private readonly SettingsService _settings;
    private readonly ExportService _export;
    private readonly Dictionary<string, ConnectionRow> _byKey = new();
    private bool _suspend;

    public LiveConnectionsViewModel(MonitoringService monitoring, SettingsService settings, ExportService export)
    {
        _monitoring = monitoring; _settings = settings; _export = export;
        monitoring.SnapshotUpdated += _ => { if (IsLive) Rebuild(); };
        var visible = settings.Current.VisibleColumns.Split(',').ToHashSet();
        Columns = new ObservableCollection<ColumnOption>(new[]
        {
            ("Status", "Status"), ("Process", "Process"), ("PID", "PID"), ("Protocol", "Protocol"), ("LocalAddress", "Local address"),
            ("LocalPort", "Local port"), ("RemoteAddress", "Remote address"), ("RemotePort", "Remote port"), ("State", "State"),
            ("FirstObserved", "First observed"), ("LastObserved", "Last observed"),
        }.Select(c => new ColumnOption(c.Item1, c.Item2, visible.Contains(c.Item1))));
        foreach (var c in Columns) c.PropertyChanged += (_, _) => SaveColumns();
        States = new[] { AnyState }.Concat(Enum.GetValues<TcpState>().Where(s => s is not (TcpState.Unknown or TcpState.None or TcpState.DeleteTcb)).Select(s => s.ToDisplay())).ToList();
        ReloadPresets();
    }

    public ObservableCollection<ConnectionRow> Rows { get; } = new();
    public ObservableCollection<ColumnOption> Columns { get; }
    public IReadOnlyList<string> States { get; }
    public ObservableCollection<string> Presets { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _useTcp = true;
    [ObservableProperty] private bool _useUdp = true;
    [ObservableProperty] private bool _useIPv4 = true;
    [ObservableProperty] private bool _useIPv6 = true;
    [ObservableProperty] private bool _establishedOnly;
    [ObservableProperty] private bool _listeningOnly;
    [ObservableProperty] private bool _showClosed = true;
    [ObservableProperty] private string _selectedState = AnyState;
    [ObservableProperty] private string? _selectedPreset;
    [ObservableProperty] private string _countText = "";
    [ObservableProperty] private string? _pidFilter;

    partial void OnSearchTextChanged(string value) => Rebuild();
    partial void OnUseTcpChanged(bool value) => Rebuild();
    partial void OnUseUdpChanged(bool value) => Rebuild();
    partial void OnUseIPv4Changed(bool value) => Rebuild();
    partial void OnUseIPv6Changed(bool value) => Rebuild();
    partial void OnEstablishedOnlyChanged(bool value) => Rebuild();
    partial void OnListeningOnlyChanged(bool value) => Rebuild();
    partial void OnShowClosedChanged(bool value) => Rebuild();
    partial void OnSelectedStateChanged(string value) => Rebuild();
    partial void OnPidFilterChanged(string? value) => Rebuild();
    partial void OnSelectedPresetChanged(string? value) { if (value is not null) ApplyPreset(value); }

    protected override void OnActivated() => Rebuild();

    private void SaveColumns() => _settings.Update(s => s.VisibleColumns = string.Join(",", Columns.Where(c => c.IsVisible).Select(c => c.Key)));

    public ConnectionFilter BuildFilter()
    {
        var f = new ConnectionFilter { Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText, Tcp = UseTcp, Udp = UseUdp, IPv4 = UseIPv4, IPv6 = UseIPv6,
            OnlyEstablished = EstablishedOnly, OnlyListening = ListeningOnly };
        if (TcpStateExtensions.TryParseDisplay(SelectedState, out var st) && SelectedState != AnyState) f.States.Add(st);
        if (int.TryParse(PidFilter, out var pid)) f.Pid = pid;
        if (_presetBase is not null) { f.OnlyUnknownApplications = _presetBase.OnlyUnknownApplications; f.OnlyNewlyDetected = _presetBase.OnlyNewlyDetected; foreach (var b in _presetBase.BrowserNames) f.BrowserNames.Add(b); }
        return f;
    }

    private ConnectionFilter? _presetBase;

    public void Rebuild()
    {
        if (_suspend) return;
        var latest = _monitoring.Latest;
        if (latest is null) { CountText = "Waiting for first snapshot..."; return; }
        var now = DateTime.UtcNow;
        var filter = BuildFilter();
        var source = ShowClosed ? latest.Tracker.Active.Concat(latest.Tracker.RecentlyClosed) : latest.Tracker.Active;
        var wanted = new Dictionary<string, TrackedConnection>();
        foreach (var t in source) if (filter.Matches(t, now)) wanted[t.Key] = t;

        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!wanted.ContainsKey(Rows[i].Key)) { _byKey.Remove(Rows[i].Key); Rows.RemoveAt(i); }
        foreach (var (key, t) in wanted)
        {
            if (_byKey.TryGetValue(key, out var row)) row.Update(t, now);
            else { row = new ConnectionRow(t, now); _byKey[key] = row; Rows.Add(row); }
        }
        CountText = $"{Rows.Count:N0} shown  |  {latest.Tracker.Active.Count:N0} active, {latest.Tracker.RecentlyClosed.Count:N0} recently closed";
    }

    // ---- presets ----
    private void ReloadPresets()
    {
        Presets.Clear();
        foreach (var k in FilterPresets.BuiltIn.Keys) Presets.Add(k);
        foreach (var k in LoadUserPresets().Keys) Presets.Add("* " + k);
    }

    private Dictionary<string, PresetSpec> LoadUserPresets()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, PresetSpec>>(_settings.Current.SavedFilterPresets) ?? new(); }
        catch { return new(); }
    }

    private void ApplyPreset(string name)
    {
        _suspend = true;
        try
        {
            ConnectionFilter? f = null; PresetSpec? spec = null;
            if (name.StartsWith("* ")) LoadUserPresets().TryGetValue(name[2..], out spec);
            else if (FilterPresets.BuiltIn.TryGetValue(name, out var make)) f = make();
            if (f is null && spec is null) return;
            _presetBase = f;
            SearchText = spec?.Search ?? f?.Search ?? "";
            UseTcp = spec?.Tcp ?? f!.Tcp; UseUdp = spec?.Udp ?? f!.Udp; UseIPv4 = spec?.IPv4 ?? f!.IPv4; UseIPv6 = spec?.IPv6 ?? f!.IPv6;
            EstablishedOnly = spec?.Established ?? f!.OnlyEstablished; ListeningOnly = spec?.Listening ?? f!.OnlyListening;
            SelectedState = spec?.State ?? (f!.States.Count == 1 ? f.States.First().ToDisplay() : AnyState);
            PidFilter = null;
        }
        finally { _suspend = false; }
        Rebuild();
    }

    [RelayCommand]
    private void ResetFilters()
    {
        _suspend = true;
        _presetBase = null; SearchText = ""; UseTcp = UseUdp = UseIPv4 = UseIPv6 = true; EstablishedOnly = ListeningOnly = false;
        SelectedState = AnyState; PidFilter = null; SelectedPreset = null; ShowClosed = true;
        _suspend = false;
        Rebuild();
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = PromptWindow.Ask("Save filter preset", "Preset name:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var all = LoadUserPresets();
        all[name.Trim()] = new PresetSpec(SearchText, UseTcp, UseUdp, UseIPv4, UseIPv6, EstablishedOnly, ListeningOnly, SelectedState);
        _settings.Update(s => s.SavedFilterPresets = JsonSerializer.Serialize(all));
        ReloadPresets();
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is not { } p || !p.StartsWith("* ")) return;
        var all = LoadUserPresets();
        all.Remove(p[2..]);
        _settings.Update(s => s.SavedFilterPresets = JsonSerializer.Serialize(all));
        SelectedPreset = null;
        ReloadPresets();
    }

    // ---- actions used by the view's context menu ----
    public void ShowProcessConnections(ConnectionRow r) { ResetFilters(); PidFilter = r.Pid.ToString(); }
    public void ShowRemoteConnections(ConnectionRow r) { if (r.RemoteAddress.Length > 0) { ResetFilters(); SearchText = r.RemoteAddress; } }

    public void ShowForProcess(ProcessInfo p) { ResetFilters(); PidFilter = p.Pid.ToString(); }

    public async Task ExportAsync(string scope, IList? selected, Window? owner)
    {
        var latest = _monitoring.Latest;
        if (latest is null) return;
        IEnumerable<TrackedConnection> items = scope switch
        {
            "all" => latest.Tracker.Active,
            "selected" => (selected ?? new List<ConnectionRow>()).Cast<ConnectionRow>().Select(r => r.Tracked).ToList(),
            _ => Rows.Select(r => r.Tracked).ToList(),
        };
        var title = scope switch { "all" => "All connections", "selected" => "Selected connections", _ => "Filtered connections" };
        await _export.ExportAsync(ExportBuilder.Connections(title, items), owner, "connections-" + scope);
    }

    public async Task ExportProcessSummaryAsync(Window? owner)
    {
        if (_monitoring.Latest is null) return;
        await _export.ExportAsync(ExportBuilder.ProcessSummary(_monitoring.Latest.Tracker.Active), owner, "process-summary");
    }
}
