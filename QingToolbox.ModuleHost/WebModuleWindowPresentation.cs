using System.Windows;
using System.Windows.Media;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.ModuleHost;

internal static class WebModuleWindowPresentation
{
    public static bool IsOverlay(ModuleHostWindowPresentationMode mode) =>
        mode == ModuleHostWindowPresentationMode.Overlay;

    public static void Apply(Window window, ModuleHostWindowPresentationMode mode)
    {
        var overlay = IsOverlay(mode);
        window.Width = overlay ? 1000 : 900;
        window.Height = 680;
        window.MinWidth = overlay ? 0 : 520;
        window.MinHeight = overlay ? 0 : 380;
        if (!overlay) return;

        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.ShowInTaskbar = false;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }
}
