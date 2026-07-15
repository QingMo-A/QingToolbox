using System.Globalization;
using System.Text.Json;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Core.Localization;

public sealed class LocalizationManager(UserSettingsService settingsService)
    : ILocalizationService
{
    private static readonly HashSet<string> SupportedCodes =
        new(StringComparer.OrdinalIgnoreCase) { "system", "zh-CN", "en-US" };

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>>
        _shellResources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModuleCatalog> _moduleCatalogs =
        new(StringComparer.Ordinal);

    public IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new("system", "System Default", "跟随系统"),
        new("zh-CN", "Simplified Chinese", "简体中文"),
        new("en-US", "English", "English")
    ];

    public CultureInfo CurrentCulture { get; private set; } =
        CultureInfo.GetCultureInfo("en-US");

    public string CurrentLanguageCode => CurrentCulture.Name;
    public string ConfiguredLanguageCode { get; private set; } = "system";
    public event EventHandler? CultureChanged;

    public async Task InitializeAsync(string resourcesDirectory)
    {
        foreach (var code in new[] { "en-US", "zh-CN" })
        {
            var resource = ReadResource(
                Path.Combine(resourcesDirectory, $"{code}.json"));
            if (resource is not null)
            {
                _shellResources[code] = resource;
            }
        }

        var settings = await settingsService.LoadAsync();
        ConfiguredLanguageCode = SupportedCodes.Contains(settings.Language)
            ? settings.Language
            : "system";
        ApplyCulture(ResolveCulture(ConfiguredLanguageCode), raiseEvent: false);
    }

    public async Task SetLanguageAsync(string languageCode)
    {
        if (!SupportedCodes.Contains(languageCode))
        {
            languageCode = "system";
        }

        var culture = ResolveCulture(languageCode);
        var changed = ConfiguredLanguageCode != languageCode ||
                      CurrentCulture.Name != culture.Name;
        ConfiguredLanguageCode = languageCode;
        var settings = await settingsService.LoadAsync();
        settings.Language = languageCode;
        await settingsService.SaveAsync(settings);
        ApplyCulture(culture, changed);
    }

    public IReadOnlyList<string> RegisterModuleLocalization(
        string moduleId,
        string moduleDirectory,
        ModuleLocalizationManifest? localization,
        string? defaultLanguage)
    {
        var diagnostics = new List<string>();
        var resources =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);

        if (localization?.Resources is not null)
        {
            var moduleRoot = Path.GetFullPath(moduleDirectory);
            var modulePrefix = moduleRoot.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (var entry in localization.Resources)
            {
                try
                {
                    var resourcePath = Path.GetFullPath(
                        Path.Combine(moduleRoot, entry.Value));
                    if (!resourcePath.StartsWith(
                            modulePrefix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(
                            $"Localization path escapes module directory: {entry.Value}");
                        continue;
                    }

                    var resource = ReadResource(resourcePath);
                    if (resource is null)
                    {
                        diagnostics.Add(
                            $"Localization resource could not be read: {entry.Value}");
                        continue;
                    }

                    resources[entry.Key] = resource;
                }
                catch (Exception exception)
                {
                    diagnostics.Add(
                        $"Localization resource '{entry.Value}' is invalid: {exception.Message}");
                }
            }
        }

        _moduleCatalogs[moduleId] = new ModuleCatalog(
            defaultLanguage,
            resources);
        return diagnostics;
    }

    public void ClearModuleLocalizations() => _moduleCatalogs.Clear();

    public string GetString(string key) =>
        FindShellString(key) ?? key;

    public string GetString(string key, params object[] args) =>
        string.Format(CurrentCulture, GetString(key), args);

    public string GetModuleString(
        string moduleId,
        string key,
        string? fallback = null)
    {
        if (_moduleCatalogs.TryGetValue(moduleId, out var catalog))
        {
            foreach (var code in GetModuleFallbackCodes(catalog.DefaultLanguage))
            {
                if (catalog.Resources.TryGetValue(code, out var resource) &&
                    resource.TryGetValue(key, out var value))
                {
                    return value;
                }
            }
        }

        return fallback ?? key;
    }

    public string GetModuleString(
        string moduleId,
        string key,
        string? fallback,
        params object[] args) =>
        string.Format(
            CurrentCulture,
            GetModuleString(moduleId, key, fallback),
            args);

    private string? FindShellString(string key)
    {
        foreach (var code in GetShellFallbackCodes())
        {
            if (_shellResources.TryGetValue(code, out var resource) &&
                resource.TryGetValue(key, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private IEnumerable<string> GetShellFallbackCodes()
    {
        yield return CurrentCulture.Name;
        if (!string.IsNullOrWhiteSpace(CurrentCulture.Parent.Name))
        {
            yield return CurrentCulture.Parent.Name;
        }
        yield return "en-US";
    }

    private IEnumerable<string> GetModuleFallbackCodes(string? defaultLanguage)
    {
        yield return CurrentCulture.Name;
        if (!string.IsNullOrWhiteSpace(CurrentCulture.Parent.Name))
        {
            yield return CurrentCulture.Parent.Name;
        }
        if (!string.IsNullOrWhiteSpace(defaultLanguage))
        {
            yield return defaultLanguage;
        }
        yield return "en-US";
    }

    private static CultureInfo ResolveCulture(string code)
    {
        if (!string.Equals(code, "system", StringComparison.OrdinalIgnoreCase))
        {
            return CultureInfo.GetCultureInfo(code);
        }

        var system = CultureInfo.InstalledUICulture;
        if (system.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return CultureInfo.GetCultureInfo("zh-CN");
        }

        return system.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("en-US")
            : CultureInfo.GetCultureInfo("en-US");
    }

    private void ApplyCulture(CultureInfo culture, bool raiseEvent)
    {
        CurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        if (raiseEvent)
        {
            CultureChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static IReadOnlyDictionary<string, string>?
        ReadResource(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }

    private sealed record ModuleCatalog(
        string? DefaultLanguage,
        IReadOnlyDictionary<
            string,
            IReadOnlyDictionary<string, string>> Resources);
}
