using System.ComponentModel;
using System.Windows;
using PortVisualizer.App.Services;
using PortVisualizer.App.ViewModels;
using PortVisualizer.Core.Settings;

namespace PortVisualizer.App;

public partial class MainWindow : Window
{
    private readonly SettingsService _settings;

    public MainWindow(ShellViewModel vm, SettingsService settings)
    {
        InitializeComponent();
        DataContext = vm;
        _settings = settings;
        IsVisibleChanged += (_, _) => vm.SetUiVisible(IsVisible && WindowState != WindowState.Minimized);
        SourceInitialized += (_, _) => Shell.ApplyDarkTitleBar(this, settings.Current.DarkTheme);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting && _settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            App.Instance.Tray?.ShowMinimizedHint();
            return;
        }
        base.OnClosing(e);
        if (!e.Cancel) App.ExitApplication();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        (DataContext as ShellViewModel)?.SetUiVisible(IsVisible && WindowState != WindowState.Minimized);
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray)
        {
            Hide();
            App.Instance.Tray?.ShowMinimizedHint();
        }
    }
}
