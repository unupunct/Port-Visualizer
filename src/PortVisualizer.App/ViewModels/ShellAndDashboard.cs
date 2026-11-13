using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using TcpState = PortVisualizer.Core.Models.TcpState;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PortVisualizer.App.Services;
using PortVisualizer.Core.Models;
using PortVisualizer.Core.Monitoring;

namespace PortVisualizer.App.ViewModels;

public enum NavTarget { Dashboard, LiveConnections, NetworkMap, Processes, History, Alerts, Settings, About }

/// <summary>Base for pages. Pages only do work while active so hidden views cost no CPU.</summary>
public abstract class PageViewModel : ObservableObject
{
    public bool IsActive { get; private set; }
    /// <summary>False while the window is hidden to the tray or minimised: pages then skip all UI work.</summary>
    public static bool UiVisible { get; set; } = true;
    protected bool IsLive => IsActive && UiVisible;
    public void Activate() { IsActive = true; OnActivated(); }
    public void Deactivate() { IsActive = false; OnDeactivated(); }
    protected virtual void OnActivated() { }
    protected virtual void OnDeactivated() { }
}

public sealed record NavItem(string Title, string Glyph, NavTarget Target, PageViewModel Page);

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly MonitoringService _monitoring;

    public ShellViewModel(MonitoringService monitoring, DashboardViewModel dash, LiveConnectionsViewModel live, NetworkMapViewModel map,
        ProcessesViewModel procs, HistoryViewModel history, AlertsViewModel alerts, SettingsViewModel settings, AboutViewModel about)
    {
        _monitoring = monitoring;
        NavItems = new[]
        {
            new NavItem("Dashboard", "", NavTarget.Dashboard, dash),
            new NavItem("Live Connections", "", NavTarget.LiveConnections, live),
            new NavItem("Network Map", "", NavTarget.NetworkMap, map),
            new NavItem("Processes", "", NavTarget.Processes, procs),
            new NavItem("Connection History", "", NavTarget.History, history),
            new NavItem("Alerts", "", NavTarget.Alerts, alerts),
            new NavItem("Settings", "", NavTarget.Settings, settings),
            new NavItem("About", "", NavTarget.About, about),
        };
        _selected = NavItems[0];
        _selected.Page.Activate();
        monitoring.StateChanged += UpdateStatus;
        monitoring.SnapshotUpdated += _ => UpdateStatus();
        monitoring.ErrorChanged += UpdateStatus;
        UpdateStatus();
    }

    public IReadOnlyList<NavItem> NavItems { get; }

    [ObservableProperty] private NavItem _selected;
    [ObservableProperty] private string _statusWord = "ACTIVE";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private string _footer = "";
    [ObservableProperty] private bool _isPaused;

    public PageViewModel CurrentPage => Selected.Page;

    partial void OnSelectedChanging(NavItem value) => Selected.Page.Deactivate();

    partial void OnSelectedChanged(NavItem value)
    {
        value.Page.Activate();
        OnPropertyChanged(nameof(CurrentPage));
    }

    public void SetUiVisible(bool visible)
    {
        if (PageViewModel.UiVisible == visible) return;
        PageViewModel.UiVisible = visible;
        if (visible) { Selected.Page.Deactivate(); Selected.Page.Activate(); }
    }

    public void Navigate(NavTarget t) => Selected = NavItems.First(n => n.Target == t);

    [RelayCommand]
    private void TogglePause()
    {
        if (_monitoring.Monitor.State == MonitorState.Running) _monitoring.Pause(); else _monitoring.Resume();
    }

    private void UpdateStatus()
    {
        var m = _monitoring.Monitor;
        IsPaused = m.State != MonitorState.Running;
        StatusWord = m.State switch
        {
            MonitorState.Paused => "WARNING",
            MonitorState.Stopped => "CLOSED",
            _ => _monitoring.LastError is not null && m.ConsecutiveFailures > 0 ? "WARNING" : "ACTIVE",
        };
        StatusDetail = m.State switch
        {
            MonitorState.Paused => "Monitoring paused",
            MonitorState.Stopped => "Monitoring stopped",
            _ => m.ConsecutiveFailures > 0 ? "Monitoring: errors" : "Monitoring active",
        };
        var last = m.LastRefreshUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "-";
        Footer = $"Last refresh {last}  |  every {m.Interval.TotalMilliseconds:0} ms  |  {_monitoring.Latest?.Tracker.Active.Count ?? 0} connections  |  local only, nothing is uploaded";
    }
}

public sealed record BarItem(string Label, int Count, double Fraction, string Color);
public sealed record TopProcessRow(string Name, int Pid, int Connections, int Tcp, int Udp, int RemoteIps);
public sealed record TopRemoteRow(string Remote, int Port, string Protocol, string Processes, string State, int Count);
public sealed record InterfaceRow(string Name, string Kind, string Addresses);

public sealed partial class DashboardViewModel : PageViewModel
{
    private readonly MonitoringService _monitoring;
    private DateTime _lastIfaces = DateTime.MinValue;

    public DashboardViewModel(MonitoringService monitoring)
    {
        _monitoring = monitoring;
        monitoring.SnapshotUpdated += _ => { if (IsLive) Refresh(); };
        monitoring.StateChanged += () => { if (IsLive) Refresh(); };
        monitoring.ErrorChanged += () => { if (IsLive) Refresh(); };
        Feed = monitoring.Feed;
    }

    public ObservableCollection<FeedItem> Feed { get; }

    [ObservableProperty] private int _tcpCount;
    [ObservableProperty] private int _udpCount;
    [ObservableProperty] private int _listeningCount;
    [ObservableProperty] private int _remoteIpCount;
    [ObservableProperty] private int _processCount;
    [ObservableProperty] private long _newToday;
    [ObservableProperty] private IReadOnlyList<BarItem> _stateBars = Array.Empty<BarItem>();
    [ObservableProperty] private IReadOnlyList<BarItem> _protocolBars = Array.Empty<BarItem>();
    [ObservableProperty] private IReadOnlyList<TopProcessRow> _topProcesses = Array.Empty<TopProcessRow>();
    [ObservableProperty] private IReadOnlyList<TopRemoteRow> _topRemotes = Array.Empty<TopRemoteRow>();
    [ObservableProperty] private IReadOnlyList<InterfaceRow> _interfaces = Array.Empty<InterfaceRow>();
    [ObservableProperty] private IReadOnlyList<ActivitySample> _activity = Array.Empty<ActivitySample>();
    [ObservableProperty] private string _monitoringStatus = "";
    [ObservableProperty] private string _lastRefresh = "never";
    [ObservableProperty] private string _errors = "None";
    [ObservableProperty] private bool _feedPaused;

    partial void OnFeedPausedChanged(bool value) => _monitoring.FeedPaused = value;

    protected override void OnActivated() { FeedPaused = _monitoring.FeedPaused; _lastRefreshAt = DateTime.MinValue; Refresh(); }

    [RelayCommand] private void ClearFeed() => _monitoring.Feed.Clear();

    private void SetIfChanged<T>(ref IReadOnlyList<T> field, string name, IReadOnlyList<T> value)
    {
        if (field.SequenceEqual(value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private static readonly string[] StateColors = { "#00E5A0", "#00D4FF", "#3B82F6", "#A855F7", "#FACC15", "#FB923C", "#8793A0" };

    private DateTime _lastRefreshAt = DateTime.MinValue;

    private void Refresh()
    {
        // The dashboard is a summary view: refreshing it more than every ~2 s only burns CPU.
        if (DateTime.UtcNow - _lastRefreshAt < TimeSpan.FromMilliseconds(1800) && Activity.Count > 0) return;
        _lastRefreshAt = DateTime.UtcNow;
        var m = _monitoring.Monitor;
        MonitoringStatus = m.State.ToString();
        LastRefresh = m.LastRefreshUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "never";
        Errors = _monitoring.LastError ?? "None";
        NewToday = _monitoring.NewConnectionsToday;
        Activity = _monitoring.Activity.ToList();

        if (_monitoring.Latest is not { } latest) return;
        var active = latest.Tracker.Active;

        TcpCount = active.Count(t => t.Connection.Protocol == Protocol.Tcp && !t.Connection.IsListening);
        UdpCount = active.Count(t => t.Connection.IsUdp);
        ListeningCount = active.Count(t => t.Connection.IsListening);
        RemoteIpCount = active.Where(t => t.Connection.HasRemote).Select(t => t.Connection.RemoteAddress).Distinct().Count();
        ProcessCount = active.Where(t => t.Connection.Process.Pid != 0).Select(t => t.Connection.Process.Key).Distinct().Count();

        var total = Math.Max(1, active.Count);
        var states = active.GroupBy(t => t.Connection.IsUdp ? "UDP (stateless)" : t.Connection.State.ToDisplay())
            .OrderByDescending(g => g.Count()).ToList();
        SetIfChanged(ref _stateBars, nameof(StateBars), states.Select((g, i) => new BarItem(g.Key, g.Count(), Math.Round(g.Count() / (double)total, 2), StateColors[i % StateColors.Length])).ToList());
        var tcp = active.Count(t => t.Connection.Protocol == Protocol.Tcp);
        var udp = active.Count - tcp;
        SetIfChanged(ref _protocolBars, nameof(ProtocolBars), new[]
        {
            new BarItem("TCP", tcp, Math.Round(tcp / (double)total, 2), "#00D4FF"),
            new BarItem("UDP", udp, Math.Round(udp / (double)total, 2), "#A855F7"),
        });

        SetIfChanged(ref _topProcesses, nameof(TopProcesses), active.Where(t => t.Connection.Process.Pid != 0).GroupBy(t => t.Connection.Process.Key)
            .Select(g =>
            {
                var p = g.First().Connection.Process;
                return new TopProcessRow(p.Name, p.Pid, g.Count(), g.Count(x => x.Connection.Protocol == Protocol.Tcp),
                    g.Count(x => x.Connection.IsUdp), g.Where(x => x.Connection.HasRemote).Select(x => x.Connection.RemoteAddress).Distinct().Count());
            }).OrderByDescending(r => r.Connections).Take(8).ToList());

        SetIfChanged(ref _topRemotes, nameof(TopRemotes), active.Where(t => t.Connection.HasRemote && t.Connection.State != TcpState.TimeWait)
            .GroupBy(t => (t.Connection.RemoteAddress, t.Connection.RemotePort, t.Connection.Protocol))
            .Select(g => new TopRemoteRow(g.Key.RemoteAddress!, g.Key.RemotePort, g.Key.Protocol.ToString().ToUpperInvariant(),
                string.Join(", ", g.Select(x => x.Connection.Process.Name).Distinct().Take(3)),
                g.GroupBy(x => x.Connection.State).OrderByDescending(s => s.Count()).First().Key.ToDisplay(), g.Count()))
            .OrderByDescending(r => r.Count).Take(8).ToList());

        if (DateTime.UtcNow - _lastIfaces > TimeSpan.FromSeconds(10))
        {
            _lastIfaces = DateTime.UtcNow;
            Interfaces = ReadInterfaces();
        }
    }

    private static bool IsFilterDriver(string name) =>
        new[] { "WFP", "QoS", "Packet Scheduler", "LightWeight Filter", "Native MAC Layer", "Npcap", "Virtual Switch Extension" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<InterfaceRow> ReadInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                            && !IsFilterDriver(n.Name) && n.GetIPProperties().UnicastAddresses.Any(a => !a.Address.IsIPv6LinkLocal))
                .Select(n => new InterfaceRow(n.Name, n.NetworkInterfaceType.ToString(),
                    string.Join(", ", n.GetIPProperties().UnicastAddresses
                        .Where(a => !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString()).Take(2))))
                .Take(6).ToList();
        }
        catch (NetworkInformationException) { return Array.Empty<InterfaceRow>(); }
    }
}
