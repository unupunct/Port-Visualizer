using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortVisualizer.App.Services;
using PortVisualizer.App.Views;
using PortVisualizer.Core.Export;
using PortVisualizer.Core.Filtering;
using PortVisualizer.Core.Graph;
using PortVisualizer.Core.Models;
using PortVisualizer.Core.Settings;

namespace PortVisualizer.App.ViewModels;

public sealed record DetailLine(string Label, string Value);

public sealed partial class NetworkMapViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private readonly SettingsService _settings;
    private readonly ReverseDnsResolver _dns = new();
    private GraphNode? _selected;

    public NetworkMapViewModel(MonitoringService monitoring, SettingsService settings)
    {
        _monitoring = monitoring; _settings = settings;
        Graph = new NetworkGraph();
        _layout = settings.Current.GraphLayout;
        Graph.SetMode(_layout);
        _animate = settings.Current.GraphAnimation;
        monitoring.SnapshotUpdated += _ => { if (IsLive) Refresh(); };
        settings.Changed += s => { Animate = s.GraphAnimation; OnPropertyChanged(nameof(CanReverseDns)); };
    }

    public NetworkGraph Graph { get; }
    public IReadOnlyList<GraphLayoutMode> Layouts { get; } = Enum.GetValues<GraphLayoutMode>();
    public ObservableCollection<DetailLine> Details { get; } = new();
    public event Action? GraphChanged;

    [ObservableProperty] private GraphLayoutMode _layout;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showListening = true;
    [ObservableProperty] private bool _showClosed = true;
    [ObservableProperty] private bool _groupRemoteByIp = true;
    [ObservableProperty] private bool _establishedOnly;
    [ObservableProperty] private bool _useTcp = true;
    [ObservableProperty] private bool _useUdp = true;
    [ObservableProperty] private bool _useIPv4 = true;
    [ObservableProperty] private bool _useIPv6 = true;
    [ObservableProperty] private bool _animate;
    [ObservableProperty] private string _detailsTitle = "Select a node";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private bool _canResolve;
    [ObservableProperty] private string _hiddenNote = "";

    public bool CanReverseDns => _settings.Current.ReverseDnsAllowed;

    partial void OnLayoutChanged(GraphLayoutMode value) { Graph.SetMode(value); GraphChanged?.Invoke(); _settings.Update(s => s.GraphLayout = value); }
    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnShowListeningChanged(bool value) => Refresh();
    partial void OnShowClosedChanged(bool value) => Refresh();
    partial void OnGroupRemoteByIpChanged(bool value) => Refresh();
    partial void OnEstablishedOnlyChanged(bool value) => Refresh();
    partial void OnUseTcpChanged(bool value) => Refresh();
    partial void OnUseUdpChanged(bool value) => Refresh();
    partial void OnUseIPv4Changed(bool value) => Refresh();
    partial void OnUseIPv6Changed(bool value) => Refresh();

    protected override void OnActivated() => Refresh();

    [RelayCommand]
    private void ResetLayout() { Graph.ResetLayout(); GraphChanged?.Invoke(); }

    public void Refresh()
    {
        var latest = _monitoring.Latest;
        if (latest is null) return;
        var now = DateTime.UtcNow;
        var f = new ConnectionFilter
        {
            Search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText, Tcp = UseTcp, Udp = UseUdp, IPv4 = UseIPv4, IPv6 = UseIPv6,
            OnlyEstablished = EstablishedOnly,
        };
        var active = latest.Tracker.Active.Where(t => f.Matches(t, now)).ToList();
        var closed = latest.Tracker.RecentlyClosed.Where(t => f.Matches(t, now)).ToList();
        var opt = new GraphOptions(GroupRemoteByIp, ShowListening, ShowClosed, TimeSpan.FromSeconds(10), _monitoring.WarningIps);
        Graph.Update(active, closed, opt, now);
        Summary = $"{Graph.Nodes.Values.Count(n => n.Kind == NodeKind.Process)} processes  |  {Graph.Nodes.Values.Count(n => n.Kind == NodeKind.Remote)} remote endpoints  |  {active.Count} connections";
        HiddenNote = Graph.HiddenRemoteCount > 0 ? $"{Graph.HiddenRemoteCount} remote endpoints hidden (node limit). Use filters to narrow." : "";
        if (_selected is not null) ShowNode(Graph.Nodes.GetValueOrDefault(_selected.Id));
        GraphChanged?.Invoke();
    }

    public void ShowNode(GraphNode? node)
    {
        _selected = node;
        Details.Clear();
        HasSelection = node is not null;
        CanResolve = false;
        if (node is null) { DetailsTitle = "Select a node"; return; }

        void Add(string l, string? v) => Details.Add(new DetailLine(l, string.IsNullOrEmpty(v) ? "-" : v));
        static string T(DateTime d) => d.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        var conns = node.Connections;
        var first = conns.Count == 0 ? (DateTime?)null : conns.Min(c => c.FirstObservedUtc);
        var last = conns.Count == 0 ? (DateTime?)null : conns.Max(c => c.LastObservedUtc);

        switch (node.Kind)
        {
            case NodeKind.Computer:
                DetailsTitle = "Local computer";
                Add("Host name", Environment.MachineName);
                Add("Active connections", node.ConnectionCount.ToString());
                Add("Processes", Graph.Nodes.Values.Count(n => n.Kind == NodeKind.Process).ToString());
                break;
            case NodeKind.Process:
            {
                var p = node.Process!;
                DetailsTitle = "Process: " + p.Name;
                Add("Application", p.Name); Add("PID", p.Pid.ToString()); Add("Executable", p.Path ?? "unavailable (access denied or protected)");
                Add("Connections", conns.Count(c => !c.IsClosed).ToString());
                Add("TCP connections", conns.Count(c => !c.IsClosed && c.Connection.Protocol == Protocol.Tcp && !c.Connection.IsListening).ToString());
                Add("UDP endpoints", conns.Count(c => !c.IsClosed && c.Connection.IsUdp).ToString());
                Add("Listening ports", conns.Count(c => !c.IsClosed && c.Connection.IsListening).ToString());
                var ips = conns.Where(c => c.Connection.HasRemote).Select(c => c.Connection.RemoteAddress!).Distinct().ToList();
                Add("Remote IPs (" + ips.Count + ")", string.Join(", ", ips.Take(12)) + (ips.Count > 12 ? ", ..." : ""));
                if (first is not null) { Add("First observed", T(first.Value)); Add("Last observed", T(last!.Value)); }
                break;
            }
            case NodeKind.Remote:
            {
                var ip = node.RemoteAddress!;
                DetailsTitle = "Remote: " + ip;
                Add("IP address", ip);
                if (IpClassifier.TryParse(ip, out var addr)) { var info = IpClassifier.Describe(addr); Add("Address type", $"{info.Version}, {info.CategoryDisplay}"); }
                var ports = conns.Select(c => c.Connection.RemotePort).Distinct().OrderBy(p => p).ToList();
                Add(ports.Count == 1 ? "Port" : "Ports", string.Join(", ", ports.Take(15)));
                Add("Protocol", string.Join(", ", conns.Select(c => c.Connection.Protocol.ToString().ToUpperInvariant()).Distinct()));
                Add("Associated process", string.Join(", ", conns.Select(c => $"{c.Connection.Process.Name} ({c.Connection.Process.Pid})").Distinct().Take(8)));
                Add("Connection state", string.Join(", ", conns.Select(c => c.Connection.State.ToDisplay()).Distinct()));
                Add("Observed connections", conns.Count.ToString());
                if (first is not null) { Add("First observed", T(first.Value)); Add("Last observed", T(last!.Value)); }
                CanResolve = CanReverseDns;
                break;
            }
            case NodeKind.Listening:
            {
                var c = conns.FirstOrDefault()?.Connection;
                DetailsTitle = "Listening: " + node.Label;
                if (c is not null)
                {
                    var ports = conns.Select(x => x.Connection.LocalPort).Distinct().OrderBy(p => p).ToList();
                    Add("Local IP", string.Join(", ", conns.Select(x => x.Connection.LocalAddress).Distinct().Take(8)));
                    Add(ports.Count == 1 ? "Local port" : $"Local ports ({ports.Count})", string.Join(", ", ports.Take(40)) + (ports.Count > 40 ? ", ..." : ""));
                    Add("Protocol", c.Protocol.ToString().ToUpperInvariant());
                    Add("Owning process", c.Process.Name); Add("PID", c.Process.Pid.ToString()); Add("Executable", c.Process.Path ?? "unavailable");
                }
                if (first is not null) { Add("First observed", T(first.Value)); Add("Last observed", T(last!.Value)); }
                break;
            }
        }
    }

    [RelayCommand]
    private void CopyDetails() => Shell.Copy(DetailsTitle + Environment.NewLine + string.Join(Environment.NewLine, Details.Select(d => $"{d.Label}: {d.Value}")));

    [RelayCommand]
    private async Task ResolveHost()
    {
        if (_selected?.RemoteAddress is not { } ip || !CanReverseDns) return;
        var name = await _dns.ResolveAsync(ip);
        Details.Add(new DetailLine("Reverse DNS", name ?? "no PTR record found"));
    }

    [RelayCommand]
    private void OpenLocation() { if (_selected?.Process is { } p) Shell.OpenLocation(p); }

    public bool SelectedIsProcess => _selected?.Kind == NodeKind.Process;
}

public sealed partial class ProcessRow : ObservableObject
{
    public ProcessRow(ProcessInfo p) { Process = p; Key = p.Key; }
    public ProcessInfo Process { get; private set; }
    public string Key { get; }
    public string Name => Process.Name;
    public int Pid => Process.Pid;
    public string Path => Process.Path ?? "(unavailable)";
    public string Started => Process.StartTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";
    public string Publisher => Process.Publisher ?? "-";
    public string Signature => Process.Signature == SignatureStatus.NotChecked ? "-" : Process.SignatureDisplay;
    public TrackedConnection[] Connections { get; private set; } = Array.Empty<TrackedConnection>();

    [ObservableProperty] private int _total;
    [ObservableProperty] private int _tcp;
    [ObservableProperty] private int _udp;
    [ObservableProperty] private int _listening;
    [ObservableProperty] private int _remoteIps;

    public void Update(ProcessInfo p, IEnumerable<TrackedConnection> conns)
    {
        Process = p;
        Connections = conns.ToArray();
        Total = Connections.Length;
        Tcp = Connections.Count(c => c.Connection.Protocol == Protocol.Tcp && !c.Connection.IsListening);
        Udp = Connections.Count(c => c.Connection.IsUdp);
        Listening = Connections.Count(c => c.Connection.IsListening);
        RemoteIps = Connections.Where(c => c.Connection.HasRemote).Select(c => c.Connection.RemoteAddress).Distinct().Count();
        OnPropertyChanged(nameof(Publisher)); OnPropertyChanged(nameof(Signature));
    }
}

public sealed partial class ProcessesViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private readonly ExportService _export;
    private readonly Dictionary<string, ProcessRow> _byKey = new();
    private readonly Func<LiveConnectionsViewModel> _live;
    private readonly Func<ShellViewModel> _shell;

    public ProcessesViewModel(MonitoringService monitoring, ExportService export, IServiceProvider sp)
    {
        _monitoring = monitoring; _export = export;
        _live = () => (LiveConnectionsViewModel)sp.GetService(typeof(LiveConnectionsViewModel))!;
        _shell = () => (ShellViewModel)sp.GetService(typeof(ShellViewModel))!;
        monitoring.SnapshotUpdated += _ => { if (IsLive) Refresh(); };
    }

    public ObservableCollection<ProcessRow> Rows { get; } = new();
    public ObservableCollection<ConnectionRow> SelectedConnections { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ProcessRow? _selected;
    [ObservableProperty] private string _countText = "";

    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnSelectedChanged(ProcessRow? value) { LoadSelectedConnections(); if (value is not null) _ = EnrichAsync(value); }
    protected override void OnActivated() => Refresh();

    private async Task EnrichAsync(ProcessRow row)
    {
        await Task.Run(() => _monitoring.Enricher.Enrich(row.Process));
        row.Update(row.Process, row.Connections);
    }

    public void Refresh()
    {
        var latest = _monitoring.Latest;
        if (latest is null) return;
        var q = SearchText.Trim();
        var groups = latest.Tracker.Active.GroupBy(t => t.Connection.Process.Key)
            .Where(g => q.Length == 0 || g.First().Connection.Process.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (g.First().Connection.Process.Path?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                        || g.First().Connection.Process.Pid.ToString() == q)
            .ToDictionary(g => g.Key, g => g.ToList());
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!groups.ContainsKey(Rows[i].Key)) { _byKey.Remove(Rows[i].Key); Rows.RemoveAt(i); }
        foreach (var (key, conns) in groups)
        {
            var p = conns[0].Connection.Process;
            if (!_byKey.TryGetValue(key, out var row)) { row = new ProcessRow(p); _byKey[key] = row; Rows.Add(row); }
            row.Update(p, conns);
        }
        CountText = $"{Rows.Count} processes with network activity";
        if (Selected is not null) LoadSelectedConnections();
    }

    private void LoadSelectedConnections()
    {
        SelectedConnections.Clear();
        if (Selected is null) return;
        var now = DateTime.UtcNow;
        foreach (var t in Selected.Connections.OrderBy(c => c.Connection.Protocol).ThenBy(c => c.Connection.LocalPort)) SelectedConnections.Add(new ConnectionRow(t, now));
    }

    [RelayCommand]
    private void OpenLocation() { if (Selected is not null) Shell.OpenLocation(Selected.Process); }

    [RelayCommand]
    private void ViewProperties()
    {
        if (Selected is null) return;
        new ProcessInfoWindow(Selected.Process, _monitoring.Enricher, Selected.Total) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    [RelayCommand]
    private void CopyInfo() { if (Selected is not null) Shell.Copy(ConnectionText.ProcessText(Selected.Process, Selected.Total)); }

    [RelayCommand]
    private void ShowInLive()
    {
        if (Selected is null) return;
        _live().ShowForProcess(Selected.Process);
        _shell().Navigate(NavTarget.LiveConnections);
    }

    [RelayCommand]
    private async Task ExportSummary()
    {
        if (_monitoring.Latest is null) return;
        await _export.ExportAsync(ExportBuilder.ProcessSummary(_monitoring.Latest.Tracker.Active), Application.Current.MainWindow, "process-summary");
    }
}
