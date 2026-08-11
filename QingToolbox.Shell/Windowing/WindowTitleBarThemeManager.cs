using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.WebShell;

namespace QingToolbox.Shell.Windowing;

/// <summary>
/// Projects the host-confirmed appearance preset into the small set of brushes
/// consumed by the native title bar.  The Web DOM is never inspected here: the
/// preset id comes from UserSettings/its host snapshot, while the existing light
/// and dark mode notification only selects the neutral variant of qing-default.
/// </summary>
internal static class WindowTitleBarThemeManager
{
    internal static void Apply(WebShellThemeMode mode, string? appearancePresetId = null)
    {
        var projection = Project(appearancePresetId, mode);
        Set("WindowTitleBarBackgroundBrush", projection.Background);
        Set("WindowTitleBarForegroundBrush", projection.Foreground);
        Set("WindowTitleBarMutedForegroundBrush", projection.MutedForeground);
        Set("WindowTitleBarDividerBrush", projection.Divider);
        Set("WindowTitleBarHoverBrush", projection.Hover);
        Set("WindowTitleBarPressedBrush", projection.Pressed);
        Set("WindowTitleBarAccentBrush", projection.Accent);
        Set("WindowTitleBarDisabledBrush", projection.Disabled);
        Set("FloatingBadgeSurfaceBrush", projection.FloatingSurface);
        Set("FloatingBadgeBorderBrush", projection.Divider);
        Set("FloatingBadgeHoverBrush", projection.Hover);
        Set("FloatingBadgePressedBrush", projection.Pressed);
        Set("FloatingBadgeFocusBrush", projection.Accent);
        Set("FloatingBadgeMenuSurfaceBrush", projection.MenuSurface);
        Set("FloatingBadgeMenuForegroundBrush", projection.Foreground);
        Set("WindowTitleActionBackgroundBrush", projection.ActionBackground);
        Set("WindowTitleActionHoverBrush", projection.ActionHover);
        Set("WindowTitleActionPressedBrush", projection.ActionPressed);
        Set("WindowTitleActionFocusBrush", projection.Accent);
        Set("WindowTitleActionDisabledBrush", projection.Disabled);
    }

    /// <summary>
    /// Pure palette projection used by the native shell and its smoke test.  The
    /// fallback is qing-default, so malformed/legacy settings remain visible and
    /// usable instead of leaving stale brushes from a previous preset behind.
    /// </summary>
    internal static WindowTitleBarThemeProjection Project(
        string? appearancePresetId,
        WebShellThemeMode mode)
    {
        var preset = AppearancePresetIds.Normalize(appearancePresetId);
        var dark = preset != AppearancePresetIds.QingDefault ||
            mode == WebShellThemeMode.Dark ||
            mode == WebShellThemeMode.System && IsSystemDark();

        return preset switch
        {
            AppearancePresetIds.NeonCircuit => new(
                "#10182B", "#E7FAFF", "#9BBBD0", "#24365A", "#12324A", "#194A64", "#5DE4FF", "#597187",
                "#E60D1527", "#111A2D", "#15213A", "#1C3153", "#194A64"),
            AppearancePresetIds.Greenline => new(
                "#0B1A10", "#E0FBE7", "#9BC5A6", "#183A22", "#113821", "#19512B", "#55F083", "#577261",
                "#E60A1710", "#0C1C11", "#102519", "#173622", "#19512B"),
            AppearancePresetIds.AuroraFlow => new(
                "#112042", "#F1F5FF", "#B8C5E7", "#477D97DE", "#3D5C76DB", "#525375CA", "#7DE7E1", "#69779D",
                "#E6142246", "#142246", "#1F315C", "#31467B", "#525375CA"),
            AppearancePresetIds.QingNova => new(
                "#0D2234", "#E9FBFF", "#ADD1D9", "#4D3BB7DC", "#332FA4CA", "#4D2C75AA", "#62E8D0", "#5F7E87",
                "#E60F2A3D", "#0D2234", "#113044", "#18435C", "#4D2C75AA"),
            _ when dark => new(
                "#162236", "#EEF5FF", "#B6C5DA", "#2B405E", "#203A65", "#2B4B7A", "#79A8FF", "#70829D",
                "#F2162236", "#162236", "#1B2A41", "#253B5C", "#2B4B7A"),
            _ => new(
                "#F8FBFF", "#10213D", "#50637E", "#DCE6F3", "#E8F0FF", "#D8E7FF", "#2563EB", "#9AA9BB",
                "#F2FFFFFF", "#FFFFFF", "#FFFFFF", "#E8F0FF", "#D8E7FF")
        };
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
        if (Application.Current is null) return;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
}

internal sealed record WindowTitleBarThemeProjection(
    string Background,
    string Foreground,
    string MutedForeground,
    string Divider,
    string Hover,
    string Pressed,
    string Accent,
    string Disabled,
    string FloatingSurface,
    string MenuSurface,
    string ActionBackground,
    string ActionHover,
    string ActionPressed);
