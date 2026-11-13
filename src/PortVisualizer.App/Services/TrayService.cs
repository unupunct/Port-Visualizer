using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows;
using PortVisualizer.App.ViewModels;
using PortVisualizer.Core.Alerts;
using PortVisualizer.Core.Monitoring;

namespace PortVisualizer.App.Services;

/// <summary>System tray icon. The badge colour is a visible indicator of whether monitoring is active.</summary>
public sealed class TrayService : IDisposable
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private readonly MonitoringService _monitoring;
    private readonly SettingsService _settings;
    private readonly System.Windows.Forms.NotifyIcon _icon = new();
    private readonly System.Windows.Forms.ToolStripMenuItem _pause = new("Pause monitoring");
    private readonly System.Windows.Forms.ToolStripMenuItem _resume = new("Resume monitoring");
    private Icon? _active, _paused, _baseIcon;
    private DateTime _lastBalloon = DateTime.MinValue;
    private bool _hintShown;

    public TrayService(MonitoringService monitoring, SettingsService settings)
    {
        _monitoring = monitoring; _settings = settings;
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _baseIcon = new Icon(stream, 32, 32);
        _active = Badge(_baseIcon, Color.FromArgb(0, 229, 160));
        _paused = Badge(_baseIcon, Color.FromArgb(135, 147, 160));

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Port Visualizer", null, (_, _) => App.ShowMainWindow());
        menu.Items.Add("Show active connections", null, (_, _) => App.ShowMainWindow(NavTarget.LiveConnections));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _pause.Click += (_, _) => Dispatch(_monitoring.Pause);
        _resume.Click += (_, _) => Dispatch(_monitoring.Resume);
        menu.Items.Add(_pause);
        menu.Items.Add(_resume);
        menu.Items.Add("View monitoring status", null, (_, _) => ShowStatus());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => App.ExitApplication());
        menu.Opening += (_, _) =>
        {
            var running = _monitoring.Monitor.State == MonitorState.Running;
            _pause.Enabled = running; _resume.Enabled = !running;
        };

        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => App.ShowMainWindow();
        _icon.Visible = true;
        Refresh();

        _monitoring.StateChanged += Refresh;
        _monitoring.SnapshotUpdated += _ => UpdateTooltip();
        _monitoring.AlertsRaised += OnAlerts;
    }

    private static void Dispatch(Action a) => System.Windows.Application.Current.Dispatcher.BeginInvoke(a);

    private static Icon Badge(Icon src, Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawIcon(src, new Rectangle(0, 0, 32, 32));
            using var ring = new SolidBrush(Color.FromArgb(255, 11, 13, 16));
            using var dot = new SolidBrush(color);
            g.FillEllipse(ring, 17, 17, 15, 15);
            g.FillEllipse(dot, 19, 19, 11, 11);
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); } finally { DestroyIcon(h); }
    }

    private void Refresh()
    {
        var running = _monitoring.Monitor.State == MonitorState.Running;
        _icon.Icon = running ? _active : _paused;
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        var state = _monitoring.Monitor.State switch { MonitorState.Running => "monitoring active", MonitorState.Paused => "monitoring PAUSED", _ => "monitoring stopped" };
        var n = _monitoring.Latest?.Tracker.Active.Count;
        var text = $"Port Visualizer - {state}" + (n is null ? "" : $" ({n} connections)");
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public string StatusText()
    {
        var m = _monitoring.Monitor;
        var last = m.LastRefreshUtc?.ToLocalTime().ToString("HH:mm:ss") ?? "never";
        return $"Monitoring: {m.State}\nInterval: {m.Interval.TotalMilliseconds:0} ms\nLast refresh: {last}\nActive connections: {_monitoring.Latest?.Tracker.Active.Count ?? 0}\n" +
               (m.LastError is null ? "Errors: none" : "Last error: " + m.LastError);
    }

    private void ShowStatus() => _icon.ShowBalloonTip(5000, "Port Visualizer status", StatusText(), System.Windows.Forms.ToolTipIcon.Info);

    public void ShowMinimizedHint()
    {
        if (_hintShown) return;
        _hintShown = true;
        _icon.ShowBalloonTip(4000, "Port Visualizer is still running",
            "Monitoring continues in the system tray. Use the tray menu to pause or exit.", System.Windows.Forms.ToolTipIcon.Info);
    }

    private void OnAlerts(IReadOnlyList<Alert> alerts)
    {
        if (!_settings.Current.NotificationsEnabled || alerts.Count == 0) return;
        if (App.MainWindowIsActive) return;
        if (DateTime.UtcNow - _lastBalloon < TimeSpan.FromSeconds(5)) return;
        _lastBalloon = DateTime.UtcNow;
        var top = alerts.OrderByDescending(a => a.Severity).First();
        var more = alerts.Count > 1 ? $"\n(+{alerts.Count - 1} more)" : "";
        _icon.ShowBalloonTip(5000, $"Port Visualizer: {top.Rule.Name}", top.Message + more,
            top.Severity == AlertSeverity.Critical ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
