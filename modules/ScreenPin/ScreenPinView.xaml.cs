using System.Windows;
using System.Windows.Controls;

namespace QingToolbox.Modules.ScreenPin;

public partial class ScreenPinView : UserControl
{
    private readonly ScreenPinManager _manager;

    public ScreenPinView(ScreenPinManager manager)
    {
        InitializeComponent();
        _manager = manager;
        _manager.CountChanged += OnCountChanged;
        Unloaded += (_, _) => _manager.CountChanged -= OnCountChanged;
        UpdateCount();
    }

    private async void OnCapture(object sender, RoutedEventArgs e) =>
        await _manager.CaptureRegionAsync();
    private void OnCloseAll(object sender, RoutedEventArgs e) => _manager.CloseAll();
    private void OnCountChanged(object? sender, EventArgs e) => UpdateCount();
    private void UpdateCount() => CountText.Text = $"Pinned images: {_manager.Count}";
}
