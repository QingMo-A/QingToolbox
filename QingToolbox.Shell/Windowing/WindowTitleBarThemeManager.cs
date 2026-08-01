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
        Set("WindowTitleBarBackgroundBrush", dark ? "#162236" : "#F7FBFF");
        Set("WindowTitleBarForegroundBrush", dark ? "#EEF5FF" : "#172033");
        Set("WindowTitleBarMutedForegroundBrush", dark ? "#B6C5DA" : "#64748B");
        Set("WindowTitleBarDividerBrush", dark ? "#2B405E" : "#D9E2EF");
        Set("WindowTitleBarHoverBrush", dark ? "#203A65" : "#EAF2FF");
        Set("WindowTitleBarPressedBrush", dark ? "#2B4B7A" : "#D8E7FF");
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
