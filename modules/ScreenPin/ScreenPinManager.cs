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
        var region = overlay.ShowDialog() == true ? overlay.SelectedRegion : null;
        if (region is null || region.Value.Width < 2 || region.Value.Height < 2)
        {
            return;
        }

        await Task.Delay(100);
        var image = ScreenCaptureService.Capture(region.Value);
        var window = new PinnedImageWindow(image);
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
