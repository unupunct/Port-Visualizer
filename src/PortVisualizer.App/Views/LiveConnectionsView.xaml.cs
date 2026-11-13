using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using PortVisualizer.App.Services;
using PortVisualizer.App.ViewModels;

namespace PortVisualizer.App.Views;

public partial class LiveConnectionsView : UserControl
{
    private LiveConnectionsViewModel? _vm;
    private readonly Dictionary<string, DataGridColumn> _columns = new();

    public LiveConnectionsView()
    {
        InitializeComponent();
        _columns["Status"] = ColStatus; _columns["Process"] = ColProcess; _columns["PID"] = ColPID; _columns["Protocol"] = ColProtocol;
        _columns["LocalAddress"] = ColLocalAddress; _columns["LocalPort"] = ColLocalPort; _columns["RemoteAddress"] = ColRemoteAddress;
        _columns["RemotePort"] = ColRemotePort; _columns["State"] = ColState; _columns["FirstObserved"] = ColFirstObserved; _columns["LastObserved"] = ColLastObserved;
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) foreach (var c in _vm.Columns) c.PropertyChanged -= OnColumnChanged;
            _vm = DataContext as LiveConnectionsViewModel;
            if (_vm is null) return;
            foreach (var c in _vm.Columns) c.PropertyChanged += OnColumnChanged;
            ApplyColumns();
        };
        Unloaded += (_, _) => { if (_vm is not null) foreach (var c in _vm.Columns) c.PropertyChanged -= OnColumnChanged; };
    }

    private void OnColumnChanged(object? s, PropertyChangedEventArgs e) => ApplyColumns();

    private void ApplyColumns()
    {
        if (_vm is null) return;
        foreach (var c in _vm.Columns)
            if (_columns.TryGetValue(c.Key, out var col)) col.Visibility = c.IsVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private ConnectionRow? Row(object sender) => Grid.SelectedItem as ConnectionRow;

    private void Grid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d is not null && d is not DataGridRow) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        if (d is DataGridRow row && !row.IsSelected) { Grid.SelectedItems.Clear(); row.IsSelected = true; }
        else if (d is null) e.Handled = true; // no menu on empty area / headers
    }
    private void ColumnsButton_Click(object sender, RoutedEventArgs e) => ColumnsPopup.IsOpen = true;
    private void ExportButton_Click(object sender, RoutedEventArgs e) => ExportPopup.IsOpen = true;

    private void CopyIp_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } r) Shell.Copy(r.RemoteAddress.Length > 0 ? r.RemoteAddress : r.LocalAddress);
    }
    private void CopyLocal_Click(object sender, RoutedEventArgs e) { if (Row(sender) is { } r) Shell.Copy(r.Tracked.Connection.LocalEndpoint); }
    private void CopyRemote_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } r) Shell.Copy(r.Tracked.Connection.RemoteEndpoint ?? "(no remote endpoint: UDP sockets are connectionless)");
    }
    private void CopyDetails_Click(object sender, RoutedEventArgs e) { if (Row(sender) is { } r) Shell.Copy(ConnectionText.Details(r.Tracked)); }
    private void OpenLocation_Click(object sender, RoutedEventArgs e) { if (Row(sender) is { } r) Shell.OpenLocation(r.Process); }
    private void ProcessInfo_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } r || _vm is null) return;
        var enricher = App.Instance.Services.GetService(typeof(MonitoringService)) as MonitoringService;
        new ProcessInfoWindow(r.Process, enricher!.Enricher, Math.Max(1, _vm.Rows.Count(x => x.Pid == r.Pid))) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
    private void RelatedProcess_Click(object sender, RoutedEventArgs e) { if (Row(sender) is { } r) _vm?.ShowProcessConnections(r); }
    private void RelatedRemote_Click(object sender, RoutedEventArgs e) { if (Row(sender) is { } r) _vm?.ShowRemoteConnections(r); }

    private async void ExportAll_Click(object sender, RoutedEventArgs e) { ExportPopup.IsOpen = false; if (_vm is not null) await _vm.ExportAsync("all", null, Window.GetWindow(this)); }
    private async void ExportFiltered_Click(object sender, RoutedEventArgs e) { ExportPopup.IsOpen = false; if (_vm is not null) await _vm.ExportAsync("filtered", null, Window.GetWindow(this)); }
    private async void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        ExportPopup.IsOpen = false;
        if (_vm is null) return;
        if (Grid.SelectedItems.Count == 0) { MessageBox.Show("Select one or more rows first.", "Export", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        await _vm.ExportAsync("selected", Grid.SelectedItems.Cast<object>().ToList(), Window.GetWindow(this));
    }
    private async void ExportSummary_Click(object sender, RoutedEventArgs e) { ExportPopup.IsOpen = false; if (_vm is not null) await _vm.ExportProcessSummaryAsync(Window.GetWindow(this)); }
}
