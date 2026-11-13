using System.Windows;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PortVisualizer.App.Services;
using PortVisualizer.App.ViewModels;
using PortVisualizer.App.Views;
using PortVisualizer.Core.Monitoring;
using PortVisualizer.Core.Native;
using PortVisualizer.Core.Processes;
using PortVisualizer.Core.Settings;
using PortVisualizer.Data;
using Serilog;

namespace PortVisualizer.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private ServiceProvider? _services;
    private TrayService? _tray;
    private bool _exiting;

    public static App Instance => (App)Current;
    public static bool IsExiting => Instance._exiting;
    public static bool MainWindowIsActive => Current?.MainWindow is { IsVisible: true, IsActive: true };
    public IServiceProvider Services => _services!;
    public TrayService? Tray => _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\PortVisualizer.SingleInstance", out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\PortVisualizer.Show");
        if (!isFirst)
        {
            _showSignal.Set(); // ask the running instance to come to the foreground
            _exiting = true;
            Shutdown();
            return;
        }

        Directory.CreateDirectory(AppPaths.DataDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppPaths.LogDirectory, "portvisualizer-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7, fileSizeLimitBytes: 5_000_000, rollOnFileSizeLimit: true)
            .CreateLogger();
        Log.Information("Port Visualizer {Version} starting", typeof(App).Assembly.GetName().Version);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled UI exception");
            MessageBox.Show("An unexpected error occurred and was logged:\n\n" + args.Exception.Message + "\n\nLogs: " + AppPaths.LogDirectory,
                "Port Visualizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Fatal(args.ExceptionObject as Exception, "Fatal exception");
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error(args.Exception, "Unobserved task exception"); args.SetObserved(); };

        try
        {
            var bootstrap = Bootstrap.Load();
            var dbPath = string.IsNullOrWhiteSpace(bootstrap.DatabasePath) ? AppPaths.DefaultDatabasePath : bootstrap.DatabasePath;
            var db = new PortVisualizerDatabase(dbPath);
            db.Migrate();
            _services = BuildServices(db, bootstrap);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show("Port Visualizer could not open its database:\n\n" + ex.Message +
                "\n\nIf you moved the database location, delete " + AppPaths.BootstrapFile + " to return to the default.",
                "Port Visualizer", MessageBoxButton.OK, MessageBoxImage.Error);
            _exiting = true;
            Shutdown(1);
            return;
        }

        var settings = _services.GetRequiredService<SettingsService>();
        ApplyTheme(settings.Current.DarkTheme, settings.Current.AccentColor);
        settings.Changed += s => ApplyTheme(s.DarkTheme, s.AccentColor);

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        _tray = _services.GetRequiredService<TrayService>();
        _services.GetRequiredService<MonitoringService>().Start();
        window.Show();
        foreach (var a in e.Args)
            if (a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<NavTarget>(a[7..], true, out var page))
                _services.GetRequiredService<ShellViewModel>().Navigate(page);

        var t = new Thread(() =>
        {
            while (!_exiting) { if (_showSignal.WaitOne(500)) Dispatcher.BeginInvoke(() => ShowMainWindow()); }
        }) { IsBackground = true, Name = "ShowSignal" };
        t.Start();
    }

    private static ServiceProvider BuildServices(PortVisualizerDatabase db, Bootstrap bootstrap)
    {
        var s = new ServiceCollection();
        s.AddLogging(b => b.AddSerilog(Log.Logger, dispose: false));
        s.AddSingleton(db);
        s.AddSingleton(bootstrap);
        s.AddSingleton<HistoryRepository>();
        s.AddSingleton<AlertRuleRepository>();
        s.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        s.AddSingleton<SettingsService>();
        s.AddSingleton<IConnectionSource, WindowsConnectionSource>();
        s.AddSingleton<IProcessApi, WindowsProcessApi>();
        s.AddSingleton<ProcessResolver>();
        s.AddSingleton<ConnectionTracker>();
        s.AddSingleton<ProcessEnricher>();
        s.AddSingleton<ExportService>();
        s.AddSingleton(sp => new HistoryWriter(sp.GetRequiredService<HistoryRepository>(),
            onError: ex => sp.GetRequiredService<ILoggerFactory>().CreateLogger("HistoryWriter").LogError(ex, "History write failed")));
        s.AddSingleton(sp => new NetworkMonitor(sp.GetRequiredService<IConnectionSource>(), sp.GetRequiredService<ProcessResolver>(),
            sp.GetRequiredService<ConnectionTracker>(), sp.GetRequiredService<ILoggerFactory>().CreateLogger("Monitor")));
        s.AddSingleton<MonitoringService>();
        s.AddSingleton<TrayService>();
        s.AddSingleton<ShellViewModel>();
        s.AddSingleton<DashboardViewModel>();
        s.AddSingleton<LiveConnectionsViewModel>();
        s.AddSingleton<NetworkMapViewModel>();
        s.AddSingleton<ProcessesViewModel>();
        s.AddSingleton<HistoryViewModel>();
        s.AddSingleton<AlertsViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<AboutViewModel>();
        s.AddSingleton<MainWindow>();
        return s.BuildServiceProvider();
    }

    public static void ApplyTheme(bool dark, string accentHex)
    {
        var res = Current.Resources;
        var dicts = res.MergedDictionaries;
        var uri = new Uri(dark ? "Themes/Theme.Dark.xaml" : "Themes/Theme.Light.xaml", UriKind.Relative);
        dicts[0] = new ResourceDictionary { Source = uri };
        try
        {
            var c = (System.Windows.Media.Color)ColorConverter.ConvertFromString(accentHex);
            res["AccentBrush"] = new SolidColorBrush(c);
            res["AccentColor"] = c;
        }
        catch (FormatException) { }
        if (Current.MainWindow is { } w) Shell.ApplyDarkTitleBar(w, dark);
    }

    public static void ShowMainWindow(NavTarget? target = null)
    {
        var w = Current.MainWindow;
        if (w is null) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true; w.Topmost = false;
        if (target is { } t) Instance.Services.GetRequiredService<ShellViewModel>().Navigate(t);
    }

    public static void ExitApplication()
    {
        var app = Instance;
        if (app._exiting) return;
        app._exiting = true;
        app.Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        try
        {
            if (_services is not null)
            {
                _tray?.Dispose();
                var monitor = _services.GetRequiredService<NetworkMonitor>();
                monitor.StopAsync().Wait(TimeSpan.FromSeconds(3));
                _services.GetRequiredService<HistoryWriter>().DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                PortVisualizerDatabase.ReleaseFiles();
                _services.Dispose();
            }
        }
        catch (Exception ex) { Log.Error(ex, "Error during shutdown"); }
        Log.Information("Port Visualizer exited");
        Log.CloseAndFlush();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
