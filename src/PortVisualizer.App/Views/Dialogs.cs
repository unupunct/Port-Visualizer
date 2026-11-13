using System.Windows;
using System.Windows.Controls;
using PortVisualizer.App.Services;
using PortVisualizer.Core.Models;
using PortVisualizer.Core.Processes;

namespace PortVisualizer.App.Views;

/// <summary>Minimal single-line text prompt styled with the app theme.</summary>
public sealed class PromptWindow : Window
{
    private readonly TextBox _box = new();

    private PromptWindow(string title, string label, string initial)
    {
        Title = title; Width = 380; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Owner = Application.Current.MainWindow; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgBrush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont");
        _box.Text = initial; _box.Margin = new Thickness(0, 8, 0, 14);
        var ok = new Button { Content = "Save", IsDefault = true, Style = (Style)Application.Current.FindResource("PrimaryButton"), Padding = new Thickness(18, 6, 18, 6) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0), Padding = new Thickness(14, 6, 14, 6) };
        ok.Click += (_, _) => DialogResult = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } };
        Content = new StackPanel { Margin = new Thickness(20), Children = { new TextBlock { Text = label }, _box, buttons } };
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); Shell.ApplyDarkTitleBar(this, true); };
    }

    public static string? Ask(string title, string label, string initial)
    {
        var w = new PromptWindow(title, label, initial);
        return w.ShowDialog() == true ? w._box.Text : null;
    }
}

/// <summary>Detailed process view with on-demand publisher and signature lookup.</summary>
public sealed class ProcessInfoWindow : Window
{
    private readonly ProcessInfo _p;
    private readonly TextBlock _body = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = 12.5, LineHeight = 20 };
    private readonly int _connections;

    public ProcessInfoWindow(ProcessInfo p, ProcessEnricher enricher, int connections)
    {
        _p = p; _connections = connections;
        Title = "Process information - " + p.Name; Width = 640; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("UiFont");

        var open = new Button { Content = "Open location" }; open.Click += (_, _) => Shell.OpenLocation(p);
        var props = new Button { Content = "File properties" }; props.Click += (_, _) => Shell.FileProperties(p);
        var copy = new Button { Content = "Copy info" }; copy.Click += (_, _) => Shell.Copy(ConnectionText.ProcessText(p, connections));
        var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(0) }; close.Click += (_, _) => Close();
        var icon = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 14, 0), Source = IconLoader.Get(p.Path) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14), Children = { icon,
            new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                new TextBlock { Text = p.Name, FontSize = 20, FontWeight = FontWeights.SemiBold },
                new TextBlock { Text = $"PID {p.Pid}", Opacity = 0.7 } } } } };
        var dock = new DockPanel { Margin = new Thickness(22) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Children = { open, props, copy, close } };
        DockPanel.SetDock(header, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(header); dock.Children.Add(buttons); dock.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = dock;
        Render();
        Loaded += async (_, _) =>
        {
            Shell.ApplyDarkTitleBar(this, true);
            _body.Text += "\n\n(checking signature...)";
            await Task.Run(() => enricher.Enrich(p));
            Render();
        };
    }

    private void Render()
    {
        _body.Text = ConnectionText.ProcessText(_p, _connections)
            + (_p.AccessDenied ? "\n\nNote: Windows denied access to this process's details (protected or elevated process). Running Port Visualizer as administrator may reveal more." : "")
            + "\n\nIdentity key: " + _p.Key + "  (PID + start time, so a reused PID is never confused with the old process)";
    }
}

public static class IconLoader
{
    private static readonly Dictionary<string, System.Windows.Media.ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static System.Windows.Media.ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (Cache.TryGetValue(path, out var hit)) return hit;
        System.Windows.Media.ImageSource? src = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
            }
        }
        catch { }
        Cache[path] = src;
        return src;
    }
}
