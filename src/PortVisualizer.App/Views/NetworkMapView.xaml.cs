using System.Windows;
using System.Windows.Controls;
using PortVisualizer.App.ViewModels;

namespace PortVisualizer.App.Views;

public partial class NetworkMapView : UserControl
{
    private NetworkMapViewModel? _vm;

    public NetworkMapView()
    {
        InitializeComponent();
        Canvas.NodeSelected += n => _vm?.ShowNode(n);
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.GraphChanged -= OnGraphChanged;
            _vm = DataContext as NetworkMapViewModel;
            if (_vm is not null) _vm.GraphChanged += OnGraphChanged;
        };
        Unloaded += (_, _) => { if (_vm is not null) _vm.GraphChanged -= OnGraphChanged; };
        Loaded += (_, _) => { if (_vm is not null) { _vm.GraphChanged -= OnGraphChanged; _vm.GraphChanged += OnGraphChanged; _vm.Refresh(); } };
    }

    private void OnGraphChanged() => Canvas.Wake();
    private void Fit_Click(object sender, RoutedEventArgs e) => Canvas.FitAndFollow();
}
