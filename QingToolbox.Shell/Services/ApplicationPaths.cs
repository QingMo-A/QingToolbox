using System.IO;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.Services;

public sealed class ApplicationPaths
{
    private readonly bool _isProduction;
    private readonly ApplicationExecutionEnvironment _environment;
    public ApplicationPaths(ApplicationExecutionEnvironment environment)
    {
        _environment = environment;
        _isProduction = environment.IsProduction;
        var baseModules = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Modules"));
        if (environment.IsProduction)
        {
            RoamingRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QingToolbox");
            LocalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingToolbox");
        }
        else
        {
            RoamingRoot = Path.Combine(environment.SandboxRoot!, "roaming");
            LocalRoot = Path.Combine(environment.SandboxRoot!, "local");
        }
        SettingsPath = Path.Combine(RoamingRoot, "settings.json");
        UserModulesDirectory = Path.Combine(LocalRoot, environment.IsProduction ? "Modules" : "modules");
        ImportedFontsDirectory = Path.Combine(LocalRoot, "Fonts", "Imported");
        ModuleDataDirectory = Path.Combine(RoamingRoot, environment.IsProduction ? "Data" : "data");
        LogsDirectory = Path.Combine(LocalRoot, "logs");
        WebView2UserDataDirectory = Path.Combine(LocalRoot, "webview2", environment.ProfileName);
        CacheDirectory = Path.Combine(LocalRoot, "cache");
        FontCacheDirectory = Path.Combine(CacheDirectory, "Fonts");
        FontCatalogCachePath = Path.Combine(FontCacheDirectory, "system-fonts.v1.json");
        HostUpdateCachePath = Path.Combine(CacheDirectory, "HostUpdates", "official-releases.json");
        HostUpdateInstallerDirectory = Path.Combine(CacheDirectory, "HostUpdates", "Installers");
        QmodStagingDirectory = Path.Combine(CacheDirectory, "ModulePackages", "Staging");
        ModuleTransactionsDirectory = Path.Combine(CacheDirectory, "ModuleTransactions");
        ModuleTransactionJournalDirectory = Path.Combine(ModuleTransactionsDirectory, "Journal");
        ModuleTransactionLocksDirectory = Path.Combine(ModuleTransactionsDirectory, "Locks");
        ModuleTransactionWorkDirectory = Path.Combine(UserModulesDirectory, ".qing-transactions");
        TempDirectory = environment.IsProduction ? Path.Combine(LocalRoot, "Temp") : Path.Combine(environment.SandboxRoot!, "temp");
        StartupDirectory = Path.Combine(LocalRoot, "Startup");
        StartupHealthPath = Path.Combine(StartupDirectory, "startup-health.json");
        DevelopmentModulesDirectory = baseModules;
        var discovery = environment.IsModuleTest ? [UserModulesDirectory] : new[] { baseModules, UserModulesDirectory };
        ModuleDiscoveryDirectories = discovery.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public string RoamingRoot { get; }
    public string LocalRoot { get; }
    public string SettingsPath { get; }
    public string DevelopmentModulesDirectory { get; }
    public string UserModulesDirectory { get; }
    /// <summary>Environment-scoped, host-owned copies of user imported fonts.</summary>
    public string ImportedFontsDirectory { get; }
    public string ModuleDataDirectory { get; }
    public string LogsDirectory { get; }
    public string WebView2UserDataDirectory { get; }
    public string CacheDirectory { get; }
    /// <summary>Environment-isolated cache for the enumerated system font catalog.</summary>
    public string FontCacheDirectory { get; }
    public string FontCatalogCachePath { get; }
    public string HostUpdateCachePath { get; }
    public string HostUpdateInstallerDirectory { get; }
    public string QmodStagingDirectory { get; }
    public string ModuleTransactionsDirectory { get; }
    public string ModuleTransactionJournalDirectory { get; }
    public string ModuleTransactionLocksDirectory { get; }
    public string ModuleTransactionWorkDirectory { get; }
    public string TempDirectory { get; }
    public string StartupDirectory { get; }
    public string StartupHealthPath { get; }
    public IReadOnlyList<string> ModuleDiscoveryDirectories { get; }

    public void EnsureDirectories()
    {
        if (!_isProduction)
            ApplicationExecutionEnvironment.AssertNoSandboxReparsePoints(
                _environment.Kind, _environment.ProfileName, _environment.RepositoryRoot!, _environment.SandboxRoot!);
        var directories = _isProduction
            ? new[] { UserModulesDirectory, ModuleDataDirectory, StartupDirectory, ImportedFontsDirectory, FontCacheDirectory }
            : new[] { RoamingRoot, LocalRoot, UserModulesDirectory,
                ModuleDataDirectory, LogsDirectory, CacheDirectory, TempDirectory, ImportedFontsDirectory, FontCacheDirectory };
        foreach (var directory in directories)
            Directory.CreateDirectory(directory);
        if (!_isProduction)
            ApplicationExecutionEnvironment.AssertNoSandboxReparsePoints(
                _environment.Kind, _environment.ProfileName, _environment.RepositoryRoot!, _environment.SandboxRoot!);
    }
}
