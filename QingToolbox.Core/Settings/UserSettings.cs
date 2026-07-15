namespace QingToolbox.Core.Settings;

public sealed class UserSettings
{
    public int SettingsSchemaVersion { get; set; } = 1;
    public string Language { get; set; } = "system";
    public double? FloatingBadgeLeft { get; set; }
    public double? FloatingBadgeTop { get; set; }
    public bool HasFloatingBadgePosition { get; set; }
    public string? FloatingBadgeMonitorDeviceName { get; set; }
    public double? FloatingBadgeHorizontalRatio { get; set; }
    public double? FloatingBadgeVerticalRatio { get; set; }

    internal void Normalize()
    {
        SettingsSchemaVersion = Math.Max(1, SettingsSchemaVersion);
        Language = string.IsNullOrWhiteSpace(Language) ? "system" : Language;
        FloatingBadgeLeft = FiniteOrNull(FloatingBadgeLeft);
        FloatingBadgeTop = FiniteOrNull(FloatingBadgeTop);
        FloatingBadgeHorizontalRatio = RatioOrNull(FloatingBadgeHorizontalRatio);
        FloatingBadgeVerticalRatio = RatioOrNull(FloatingBadgeVerticalRatio);
        FloatingBadgeMonitorDeviceName = string.IsNullOrWhiteSpace(FloatingBadgeMonitorDeviceName)
            ? null
            : FloatingBadgeMonitorDeviceName.Trim();

        if (FloatingBadgeLeft is null || FloatingBadgeTop is null)
            HasFloatingBadgePosition = false;
    }

    private static double? FiniteOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static double? RatioOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : null;
}
