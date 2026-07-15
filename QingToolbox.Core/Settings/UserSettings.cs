namespace QingToolbox.Core.Settings;

public sealed class UserSettings
{
    public int SettingsSchemaVersion { get; set; } = 2;
    public string Language { get; set; } = "system";
    public double? FloatingBadgeLeft { get; set; }
    public double? FloatingBadgeTop { get; set; }
    public bool HasFloatingBadgePosition { get; set; }
    public string? FloatingBadgeMonitorDeviceName { get; set; }
    public double? FloatingBadgeHorizontalRatio { get; set; }
    public double? FloatingBadgeVerticalRatio { get; set; }
    public bool LaunchAtLogin { get; set; }
    public StartupPresentationMode StartupPresentationMode { get; set; } = StartupPresentationMode.FloatingBadge;
    public List<StartupModuleAuthorization> StartupModules { get; set; } = [];

    internal void Normalize()
    {
        SettingsSchemaVersion = Math.Max(2, SettingsSchemaVersion);
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

        StartupModules ??= [];
        StartupModules = StartupModules
            .Where(item => !string.IsNullOrWhiteSpace(item.ModuleId))
            .Select(item => item.Normalized())
            .GroupBy(item => item.ModuleId, StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToList();
    }

    private static double? FiniteOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static double? RatioOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : null;
}

public enum StartupPresentationMode { MainWindow, Minimized, FloatingBadge }

public sealed class StartupModuleAuthorization
{
    public string ModuleId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ManifestSha256 { get; set; } = string.Empty;
    public string EntryAssemblySha256 { get; set; } = string.Empty;
    public bool ActivateOnStartup { get; set; } = true;

    internal StartupModuleAuthorization Normalized() => new()
    {
        ModuleId = ModuleId.Trim(),
        Version = Version.Trim(),
        ManifestSha256 = ManifestSha256.Trim().ToUpperInvariant(),
        EntryAssemblySha256 = EntryAssemblySha256.Trim().ToUpperInvariant(),
        ActivateOnStartup = ActivateOnStartup
    };
}
