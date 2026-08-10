using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using QingToolbox.Shell.WebShell;

namespace QingToolbox.Shell.Windowing;

internal static class WindowTitleBarThemeManager
{
    public static void Apply(WebShellThemeMode mode)
    {
        var dark = mode == WebShellThemeMode.Dark || mode == WebShellThemeMode.System && IsSystemDark();
        Set("WindowTitleBarBackgroundBrush", dark ? "#162236" : "#F8FBFF");
        Set("WindowTitleBarForegroundBrush", dark ? "#EEF5FF" : "#10213D");
        Set("WindowTitleBarMutedForegroundBrush", dark ? "#B6C5DA" : "#50637E");
        Set("WindowTitleBarDividerBrush", dark ? "#2B405E" : "#DCE6F3");
        Set("WindowTitleBarHoverBrush", dark ? "#203A65" : "#E8F0FF");
        Set("WindowTitleBarPressedBrush", dark ? "#2B4B7A" : "#D8E7FF");
        Set("WindowTitleBarAccentBrush", dark ? "#79A8FF" : "#2563EB");
        Set("FloatingBadgeSurfaceBrush", dark ? "#F2162236" : "#F2FFFFFF");
        Set("FloatingBadgeBorderBrush", dark ? "#2B405E" : "#DCE6F3");
        Set("FloatingBadgeHoverBrush", dark ? "#203A65" : "#E8F0FF");
        Set("FloatingBadgePressedBrush", dark ? "#2B4B7A" : "#D8E7FF");
        Set("FloatingBadgeFocusBrush", dark ? "#79A8FF" : "#2563EB");
        Set("FloatingBadgeMenuSurfaceBrush", dark ? "#162236" : "#FFFFFF");
        Set("FloatingBadgeMenuForegroundBrush", dark ? "#EEF5FF" : "#10213D");
    }

    internal static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static void Set(string key, string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
}
