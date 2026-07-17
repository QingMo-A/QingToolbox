namespace QingToolbox.Core.Settings;

public sealed class UserSettings
{
    public int SettingsSchemaVersion { get; set; } = 4;
    public string Language { get; set; } = "system";
    public double? FloatingBadgeLeft { get; set; }
    public double? FloatingBadgeTop { get; set; }
    public bool HasFloatingBadgePosition { get; set; }
    public string? FloatingBadgeMonitorDeviceName { get; set; }
    public double? FloatingBadgeHorizontalRatio { get; set; }
    public double? FloatingBadgeVerticalRatio { get; set; }
    public bool LaunchAtLogin { get; set; }
    public string StartupRegistrationBackend { get; set; } = "None";
    public StartupPresentationMode StartupPresentationMode { get; set; } = StartupPresentationMode.FloatingBadge;
    public MainWindowCloseBehavior MainWindowCloseBehavior { get; set; } = MainWindowCloseBehavior.Ask;
    public List<StartupModuleAuthorization> StartupModules { get; set; } = [];

    internal void Normalize()
    {
        SettingsSchemaVersion = Math.Max(4, SettingsSchemaVersion);
        Language = string.IsNullOrWhiteSpace(Language) ? "system" : Language;
        StartupRegistrationBackend = StartupRegistrationBackend is "TaskScheduler" or "RegistryRun"
            ? StartupRegistrationBackend : "None";
        FloatingBadgeLeft = FiniteOrNull(FloatingBadgeLeft);
        FloatingBadgeTop = FiniteOrNull(FloatingBadgeTop);
        FloatingBadgeHorizontalRatio = RatioOrNull(FloatingBadgeHorizontalRatio);
        FloatingBadgeVerticalRatio = RatioOrNull(FloatingBadgeVerticalRatio);
        FloatingBadgeMonitorDeviceName = string.IsNullOrWhiteSpace(FloatingBadgeMonitorDeviceName)
            ? null
            : FloatingBadgeMonitorDeviceName.Trim();
        if (!Enum.IsDefined(MainWindowCloseBehavior))
            MainWindowCloseBehavior = MainWindowCloseBehavior.Ask;

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
    public int FingerprintVersion { get; set; }
    public string PayloadSha256 { get; set; } = string.Empty;
    public int PayloadFileCount { get; set; }

    internal StartupModuleAuthorization Normalized() => new()
    {
        ModuleId = ModuleId.Trim(),
        Version = Version.Trim(),
        ManifestSha256 = ManifestSha256.Trim().ToUpperInvariant(),
        EntryAssemblySha256 = EntryAssemblySha256.Trim().ToUpperInvariant(),
        FingerprintVersion = Math.Max(0, FingerprintVersion),
        PayloadSha256 = PayloadSha256.Trim().ToUpperInvariant(),
        PayloadFileCount = Math.Max(0, PayloadFileCount),
        ActivateOnStartup = ActivateOnStartup
    };
}
