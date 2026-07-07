using System.Windows;
using System.Windows.Media.Imaging;

namespace QingToolbox.Modules.ScreenPin;

public sealed class ScreenPinManager
{
    private readonly List<PinnedImageWindow> _windows = [];
    public event EventHandler? CountChanged;
    public int Count => _windows.Count;

    public async Task CaptureRegionAsync()
    {
        var overlay = new CaptureOverlayWindow();
        var selectedRegionDip = overlay.ShowDialog() == true ? overlay.SelectedRegionDip : null;
        if (selectedRegionDip is null || selectedRegionDip.Value.Width < 2 || selectedRegionDip.Value.Height < 2)
        {
            return;
        }

        await Task.Delay(100);
        var image = ScreenCaptureService.Capture(selectedRegionDip.Value, overlay.TransformToDevice);
        var window = new PinnedImageWindow(
            image,
            selectedRegionDip.Value,
            CaptureOverlayWindow.GetVirtualScreenDip());
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            CountChanged?.Invoke(this, EventArgs.Empty);
        };
        _windows.Add(window);
        CountChanged?.Invoke(this, EventArgs.Empty);
        window.Show();
    }

    public void CloseAll()
    {
        foreach (var window in _windows.ToArray())
        {
            window.Close();
        }
    }

    public Task CloseAllAsync()
    {
        var app = Application.Current;
        if (app?.Dispatcher is null || app.Dispatcher.CheckAccess())
        {
            CloseAll();
            return Task.CompletedTask;
        }

        return app.Dispatcher.InvokeAsync(CloseAll).Task;
    }
}
