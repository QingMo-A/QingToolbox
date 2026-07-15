using System.Windows;

namespace QingToolbox.Shell.Windowing;

public static class WindowChromeMetrics
{
    public const double TitleBarHeight = 48.0;
    public const double CaptionButtonWidth = 46.0;
    public const double SystemMenuButtonWidth = 44.0;
    public static GridLength TitleBarGridLength { get; } = new(TitleBarHeight);
}
