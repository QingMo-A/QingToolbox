using System.Windows;

namespace QingToolbox.Shell.Windowing;

public readonly record struct WindowCaptionCapabilities(
    bool CanMinimize,
    bool CanMaximize)
{
    public static WindowCaptionCapabilities FromResizeMode(ResizeMode resizeMode) =>
        resizeMode switch
        {
            ResizeMode.CanResize or ResizeMode.CanResizeWithGrip => new(true, true),
            ResizeMode.CanMinimize => new(true, false),
            _ => new(false, false)
        };

    public static bool UsesRestoreAction(WindowState windowState) =>
        windowState == WindowState.Maximized;
}
