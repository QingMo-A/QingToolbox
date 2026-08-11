namespace QingToolbox.Core.Settings;

public sealed class UserSettings
{
    public int SettingsSchemaVersion { get; set; } = 9;
    public string Language { get; set; } = "system";
    public string AppearancePresetId { get; set; } = AppearancePresetIds.QingDefault;
    // Font preferences are intentionally represented by an opaque catalog id.  The
    // host resolves the id against its current system/imported catalog; no file path
    // is persisted or exposed to the Web bridge.
    public string FontId { get; set; } = FontPreferenceIds.Default;
    public string FontSource { get; set; } = FontPreferenceSources.Default;
    public string? FontFamilyName { get; set; }
    public double? FloatingBadgeLeft { get; set; }
    public double? FloatingBadgeTop { get; set; }
    public bool HasFloatingBadgePosition { get; set; }
    public string? FloatingBadgeMonitorDeviceName { get; set; }
    public double? FloatingBadgeHorizontalRatio { get; set; }
    public double? FloatingBadgeVerticalRatio { get; set; }
    public bool LaunchAtLogin { get; set; }
    public string StartupRegistrationBackend { get; set; } = "None";
    public bool StartupRegistrationCleanupPending { get; set; }
    public StartupPresentationMode StartupPresentationMode { get; set; } = StartupPresentationMode.FloatingBadge;
    public MainWindowCloseBehavior MainWindowCloseBehavior { get; set; } = MainWindowCloseBehavior.Ask;
    public bool? ShowLogsInSidebar { get; set; }
    public List<StartupModuleAuthorization> StartupModules { get; set; } = [];
    public List<string> RecentModuleIds { get; set; } = [];

    internal void Normalize()
    {
        SettingsSchemaVersion = Math.Max(9, SettingsSchemaVersion);
        Language = string.IsNullOrWhiteSpace(Language) ? "system" : Language;
        AppearancePresetId = AppearancePresetIds.Normalize(AppearancePresetId);
        FontSource = FontPreferenceSources.Normalize(FontSource);
        FontId = FontPreferenceIds.Normalize(FontId, FontSource);
        FontFamilyName = FontPreferenceIds.NormalizeFamilyName(FontFamilyName);
        if (FontId == FontPreferenceIds.Default)
        {
            FontSource = FontPreferenceSources.Default;
            FontFamilyName = null;
        }
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
        RecentModuleIds = RecentModuleHistory.Normalize(RecentModuleIds);
    }

    private static double? FiniteOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? number : null;

    private static double? RatioOrNull(double? value) =>
        value is { } number && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : null;
}

public static class FontPreferenceSources
{
    public const string Default = "default";
    public const string System = "system";
    public const string Imported = "imported";

    public static string Normalize(string? source) => source switch
    {
        System => System,
        Imported => Imported,
        _ => Default
    };
}

public static class FontPreferenceIds
{
    public const string Default = "Default";
    private const string SystemPrefix = "system:";
    private const string ImportedPrefix = "imported:";

    public static string System(string familyName) => SystemPrefix + familyName.Trim();
    public static string Imported(string sha256) => ImportedPrefix + sha256.ToLowerInvariant();

    public static string Normalize(string? id, string source)
    {
        source = FontPreferenceSources.Normalize(source);
        if (source == FontPreferenceSources.Default) return Default;
        if (string.IsNullOrWhiteSpace(id)) return Default;
        var value = id.Trim();
        if (source == FontPreferenceSources.System &&
            value.StartsWith(SystemPrefix, StringComparison.Ordinal) &&
            IsSafeFamilyName(value[SystemPrefix.Length..])) return value;
        if (source == FontPreferenceSources.Imported &&
            value.StartsWith(ImportedPrefix, StringComparison.Ordinal) &&
            IsSha256(value[ImportedPrefix.Length..])) return value.ToLowerInvariant();
        return Default;
    }

    public static string? NormalizeFamilyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return IsSafeFamilyName(normalized) ? normalized : null;
    }

    public static bool IsSafeFamilyName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(character => !char.IsControl(character) && character is not '\\' and not '/');

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

public static class AppearancePresetIds
{
    public const string QingDefault = "qing-default";
    public const string NeonCircuit = "neon-circuit";
    public const string Greenline = "greenline";
    public const string AuroraFlow = "aurora-flow";
    public const string QingNova = "qing-nova";

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        QingDefault,
        NeonCircuit,
        Greenline,
        AuroraFlow,
        QingNova
    };

    public static bool IsSupported(string? presetId) =>
        presetId is not null && Supported.Contains(presetId);

    public static string Normalize(string? presetId) =>
        IsSupported(presetId) ? presetId! : QingDefault;
}

public static class RecentModuleHistory
{
    public const int MaximumCount = 5;

    public static List<string> Normalize(IEnumerable<string?>? moduleIds) => moduleIds?
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(id => id!.Trim())
        .Distinct(StringComparer.Ordinal)
        .Take(MaximumCount)
        .ToList() ?? [];

    public static List<string> Merge(IReadOnlyList<string> persisted,
        IReadOnlyDictionary<string, long> currentSessionUses) => Normalize(
        currentSessionUses.OrderByDescending(pair => pair.Value).Select(pair => pair.Key)
            .Concat(persisted.Where(id => !currentSessionUses.ContainsKey(id))));

    public static IReadOnlyList<T> Project<T>(IEnumerable<string?> moduleIds, IEnumerable<T> modules,
        Func<T, string> idSelector)
    {
        var byId = modules.GroupBy(idSelector, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return Normalize(moduleIds).Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
    }
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
