using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingToolbox.Core;
using QingToolbox.Core.Runtime;
using QingToolbox.ModuleLoader;
using QingToolbox.Shell.Services;
using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Core.Localization;
using Microsoft.Win32;
using System.Reflection;
using QingToolbox.Core.Settings;

namespace QingToolbox.Shell.ViewModels;

public sealed partial class MainWindowViewModel(
    ModuleManifestScanner scanner,
    ModuleRegistry registry,
    ModuleRuntimeManager runtimeManager,
    ModuleWindowManager moduleWindowManager,
    ModulePackageImporter modulePackageImporter,
    ApplicationPaths applicationPaths,
    LocalizationManager localizationManager,
    UserSettingsService settingsService,
    WindowsStartupRegistrationService startupRegistrationService,
    ModuleStartupFingerprintService fingerprintService,
    ILocalizationService localization) : ObservableObject
{
    private bool _initialized;
    [ObservableProperty]
    private string _statusMessage = localization.GetString("status.ready");

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isSidebarExpanded;

    [ObservableProperty]
    private bool _isSidebarPinned;

    [ObservableProperty]
    private bool _isCompactWindow;

    [ObservableProperty]
    private string _selectedNavigationKey = "Home";

    [ObservableProperty]
    private string _selectedLanguageCode =
        localizationManager.ConfiguredLanguageCode;

    [ObservableProperty]
    private int _totalModuleCount;

    [ObservableProperty]
    private int _validModuleCount;

    [ObservableProperty]
    private int _failedModuleCount;

    [ObservableProperty]
    private int _notLoadedModuleCount;

    [ObservableProperty]
    private int _loadedModuleCount;

    [ObservableProperty]
    private int _runningModuleCount;

    [ObservableProperty]
    private int _unloadedModuleCount;

    [ObservableProperty]
    private DiscoveredModuleViewModel? _selectedModule;

    [ObservableProperty] private bool _launchAtLogin;
    [ObservableProperty] private bool _isStartupSettingsBusy;
    [ObservableProperty] private StartupPresentationMode _selectedStartupPresentationMode = StartupPresentationMode.FloatingBadge;
    [ObservableProperty] private string _startupSettingsMessage = string.Empty;

    public IReadOnlyList<StartupPresentationMode> StartupPresentationModes { get; } = Enum.GetValues<StartupPresentationMode>();

    public string Title => localization.GetString("app.name");
    public string VersionDisplay =>
        typeof(MainWindowViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.1.0-alpha";
    public LocalizedText Strings { get; } = new(localization);
    public ObservableCollection<LanguageOptionViewModel> LanguageOptions { get; } =
    [
        new("system", localization.GetString(
            "settings.language.systemDisplay")),
        new("zh-CN", localization.GetString(
            "settings.language.zhCNDisplay")),
        new("en-US", localization.GetString(
            "settings.language.enUSDisplay"))
    ];

    public string PinLabel => localization.GetString(
        IsSidebarPinned ? "sidebar.unpin" : "sidebar.pin");

    public bool IsHomeSelected => SelectedNavigationKey == "Home";
    public bool IsModulesSelected => SelectedNavigationKey == "Modules";
    public bool IsRunningSelected => SelectedNavigationKey == "Running";
    public bool IsSettingsSelected => SelectedNavigationKey == "Settings";
    public string PageTitle => SelectedNavigationKey switch
    {
        "Modules" => localization.GetString("modules.title"),
        "Running" => localization.GetString("running.title"),
        "Settings" => localization.GetString("settings.title"),
        _ => localization.GetString("app.name")
    };
    public string PageSubtitle => SelectedNavigationKey switch
    {
        "Modules" => localization.GetString("modules.subtitle"),
        "Running" => localization.GetString("running.subtitle"),
        "Settings" => localization.GetString("settings.subtitle"),
        _ => localization.GetString("app.subtitle")
    };

    public ObservableCollection<DiscoveredModuleViewModel> Modules { get; } = [];
    public ObservableCollection<DiscoveredModuleViewModel> RunningModules { get; } = [];
    public bool HasModules => Modules.Count > 0;
    public bool HasNoModules => !HasModules;
    public bool HasSelectedModule => SelectedModule is not null;

    [RelayCommand]
    private void ToggleSidebarPin()
    {
        IsSidebarPinned = !IsSidebarPinned;
        IsSidebarExpanded = IsSidebarPinned || IsSidebarExpanded;
    }

    [RelayCommand]
    private void SelectNavigation(string key)
    {
        SelectedNavigationKey = key;
    }

    [RelayCommand]
    private void OpenUserModulesDirectory()
    {
        try
        {
            Directory.CreateDirectory(applicationPaths.UserModulesDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = applicationPaths.UserModulesDirectory,
                UseShellExecute = true
            });
            StatusMessage = localization.GetString(
                "status.userModulesFolderOpened");
        }
        catch (Exception exception)
        {
            StatusMessage = localization.GetString(
                "status.userModulesFolderOpenFailed",
                exception.Message);
        }
    }

    [RelayCommand]
    private void SelectModule(DiscoveredModuleViewModel module)
    {
        SelectedModule = module;
    }

    [RelayCommand]
    private void CloseModuleDetails() => SelectedModule = null;

    partial void OnSelectedModuleChanged(DiscoveredModuleViewModel? value) =>
        OnPropertyChanged(nameof(HasSelectedModule));

    partial void OnSelectedNavigationKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsModulesSelected));
        OnPropertyChanged(nameof(IsRunningSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(PinLabel));
    }

    partial void OnSelectedLanguageCodeChanged(string value)
    {
        _ = ChangeLanguageAsync(value);
    }

    private async Task ChangeLanguageAsync(string languageCode)
    {
        await localizationManager.SetLanguageAsync(languageCode);
        foreach (var module in Modules)
        {
            module.RefreshLocalization();
        }
        moduleWindowManager.RefreshOpenWindowLocalization(moduleId =>
            Modules.FirstOrDefault(module => module.Id == moduleId)?.DisplayName);

        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(Title));
        RefreshLanguageOptionLabels();
        var option = LanguageOptions.FirstOrDefault(
            item => item.Code == languageCode);
        StatusMessage = localization.GetString(
            "status.languageChanged",
            option?.DisplayText ?? languageCode);
    }

    private void RefreshLanguageOptionLabels()
    {
        LanguageOptions[0].DisplayText = localization.GetString(
            "settings.language.systemDisplay");
        LanguageOptions[1].DisplayText = localization.GetString(
            "settings.language.zhCNDisplay");
        LanguageOptions[2].DisplayText = localization.GetString(
            "settings.language.enUSDisplay");
    }

    partial void OnIsSidebarPinnedChanged(bool value)
    {
        OnPropertyChanged(nameof(PinLabel));
    }

    [RelayCommand]
    private async Task RefreshModulesAsync()
    {
        if (IsScanning)
        {
            return;
        }

        try
        {
            IsScanning = true;
            StatusMessage = localization.GetString(
                "status.scanningModules");

            var developmentModules = await scanner.ScanAsync(
                applicationPaths.DevelopmentModulesDirectory);
            var userModules = await scanner.ScanAsync(
                applicationPaths.UserModulesDirectory);
            var discoveredModules = developmentModules
                .Concat(userModules)
                .GroupBy(module => module.Manifest.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            localizationManager.ClearModuleLocalizations();
            var localizationDiagnosticsByModuleId =
                new Dictionary<string, IReadOnlyList<string>>(
                    StringComparer.Ordinal);
            foreach (var module in discoveredModules)
            {
                var diagnostics =
                    localizationManager.RegisterModuleLocalization(
                    module.Manifest.Id,
                    module.ModuleDirectory,
                    module.Manifest.Localization,
                    module.Manifest.DefaultLanguage);
                localizationDiagnosticsByModuleId[module.Manifest.Id] =
                    diagnostics;
            }
            runtimeManager.ReplaceDiscoveredModules(discoveredModules);
            registry.ReplaceAll(discoveredModules);

            var settings = await settingsService.ReadAsync();
            var authorizations = settings.StartupModules.ToDictionary(item => item.ModuleId, StringComparer.Ordinal);
            var refreshedModules = new List<DiscoveredModuleViewModel>();
            foreach (var module in discoveredModules)
            {
                var moduleViewModel = new DiscoveredModuleViewModel(
                    module,
                    localization,
                    localizationDiagnosticsByModuleId.GetValueOrDefault(
                        module.Manifest.Id));
                moduleViewModel.UpdateRuntimeState(
                    runtimeManager.GetRecord(module.Manifest.Id));
                if (authorizations.TryGetValue(module.Manifest.Id, out var authorization))
                {
                    moduleViewModel.IsStartupEnabled = await fingerprintService.MatchesAsync(module, authorization);
                    if (!moduleViewModel.IsStartupEnabled)
                        moduleViewModel.StartupAuthorizationMessage = localization.GetString("startup.moduleChanged");
                }
                refreshedModules.Add(moduleViewModel);
            }

            var selectedModuleId = SelectedModule?.Id;
            Modules.Clear();
            foreach (var module in refreshedModules)
            {
                Modules.Add(module);
            }

            SelectedModule = Modules.FirstOrDefault(
                module => module.Id == selectedModuleId) ?? Modules.FirstOrDefault();

            UpdateStatistics();
            StatusMessage = localization.GetString(
                "status.modulesFound",
                Modules.Count);
        }
        catch (Exception exception)
        {
            UpdateStatistics();
            StatusMessage = localization.GetString(
                "status.scanFailed",
                exception.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private Task LoadModuleAsync(string moduleId)
    {
        return ExecuteLifecycleAsync(
            moduleId,
            "load",
            () => runtimeManager.LoadAsync(
                moduleId,
                applicationPaths.ModuleDataDirectory));
    }

    [RelayCommand]
    private Task ActivateModuleAsync(string moduleId)
    {
        return ExecuteLifecycleAsync(
            moduleId,
            "activate",
            () => runtimeManager.ActivateAsync(moduleId));
    }

    [RelayCommand]
    private Task DeactivateModuleAsync(string moduleId)
    {
        return ExecuteLifecycleAsync(
            moduleId,
            "deactivate",
            () => runtimeManager.DeactivateAsync(moduleId));
    }

    [RelayCommand]
    private Task UnloadModuleAsync(string moduleId)
    {
        moduleWindowManager.CloseWindow(moduleId);

        return ExecuteLifecycleAsync(
            moduleId,
            "unload",
            () => runtimeManager.UnloadAsync(moduleId));
    }

    [RelayCommand]
    private Task OpenModuleAsync(string moduleId)
    {
        var moduleViewModel = Modules.FirstOrDefault(
            module => string.Equals(module.Id, moduleId, StringComparison.Ordinal));

        if (moduleViewModel is null || moduleViewModel.IsBusy)
        {
            return Task.CompletedTask;
        }

        if (!moduleViewModel.CanOpen)
        {
            StatusMessage = localization.GetString(
                "status.moduleLoadBeforeOpen",
                moduleViewModel.DisplayName);
            return Task.CompletedTask;
        }

        moduleViewModel.IsBusy = true;
        moduleViewModel.RuntimeError = string.Empty;

        try
        {
            if (moduleWindowManager.IsWindowOpen(moduleId))
            {
                moduleWindowManager.ActivateWindow(moduleId);
                StatusMessage = localization.GetString(
                    "status.moduleFocused",
                    moduleViewModel.DisplayName);
                return Task.CompletedTask;
            }

            var view = runtimeManager.CreateView(moduleId);
            moduleViewModel.UpdateRuntimeState(runtimeManager.GetRecord(moduleId));

            if (view is null)
            {
                StatusMessage = localization.GetString(
                    "status.moduleNoView",
                    moduleViewModel.DisplayName);
                return Task.CompletedTask;
            }

            moduleWindowManager.OpenWindow(
                moduleId,
                moduleViewModel.Name,
                view,
                Application.Current.MainWindow);
            StatusMessage = localization.GetString(
                "status.moduleOpened",
                moduleViewModel.DisplayName);
        }
        catch (Exception exception)
        {
            moduleViewModel.UpdateRuntimeState(runtimeManager.GetRecord(moduleId));
            if (!moduleViewModel.HasRuntimeError)
            {
                moduleViewModel.RuntimeError = exception.Message;
            }

            StatusMessage = localization.GetString(
                "status.moduleOpenFailed",
                moduleViewModel.DisplayName,
                exception.Message);
        }
        finally
        {
            moduleViewModel.IsBusy = false;
            UpdateStatistics();
        }

        return Task.CompletedTask;
    }

    public void CloseModuleWindows() => moduleWindowManager.CloseAll();

    private async Task ExecuteLifecycleAsync(
        string moduleId,
        string operation,
        Func<Task> action)
    {
        var moduleViewModel = Modules.FirstOrDefault(
            module => string.Equals(module.Id, moduleId, StringComparison.Ordinal));

        if (moduleViewModel is null || moduleViewModel.IsBusy)
        {
            return;
        }

        moduleViewModel.IsBusy = true;
        moduleViewModel.RuntimeError = string.Empty;
        var localizedOperation = localization.GetString(
            $"common.{operation}");
        StatusMessage = localization.GetString(
            "status.moduleOperationStarted",
            localizedOperation,
            moduleViewModel.DisplayName);

        try
        {
            await action();
            moduleViewModel.UpdateRuntimeState(runtimeManager.GetRecord(moduleId));
            StatusMessage = localization.GetString(
                "status.moduleOperationCompleted",
                moduleViewModel.DisplayName,
                localizedOperation);
        }
        catch (Exception exception)
        {
            moduleViewModel.UpdateRuntimeState(runtimeManager.GetRecord(moduleId));
            if (!moduleViewModel.HasRuntimeError)
            {
                moduleViewModel.RuntimeError = exception.Message;
            }

            StatusMessage = localization.GetString(
                "status.moduleOperationFailed",
                localizedOperation,
                moduleViewModel.DisplayName,
                exception.Message);
        }
        finally
        {
            moduleViewModel.IsBusy = false;
            UpdateStatistics();
        }
    }

    private void UpdateStatistics()
    {
        TotalModuleCount = Modules.Count;
        ValidModuleCount = Modules.Count(module => module.IsValid);
        FailedModuleCount = Modules.Count(
            module => module.RuntimeState == "Failed");
        NotLoadedModuleCount = Modules.Count(
            module => module.RuntimeState == "NotLoaded");
        LoadedModuleCount = Modules.Count(
            module => module.RuntimeState is
                "Loaded" or "Running" or "Deactivated");
        RunningModuleCount = Modules.Count(
            module => module.RuntimeState == "Running");
        UnloadedModuleCount = Modules.Count(
            module => module.RuntimeState == "Unloaded");
        RunningModules.Clear();
        foreach (var module in Modules.Where(
                     module => module.RuntimeState is
                         "Loaded" or "Running" or "Deactivated" or "Unloading" or "Failed"))
        {
            RunningModules.Add(module);
        }
        OnPropertyChanged(nameof(HasModules));
        OnPropertyChanged(nameof(HasNoModules));
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        var settings = await settingsService.ReadAsync();
        try { await startupRegistrationService.ReconcileAsync(settings); }
        catch (Exception exception) { StartupSettingsMessage = exception.Message; }
        LaunchAtLogin = (await startupRegistrationService.GetStateAsync()).MatchesCurrentExecutable;
        SelectedStartupPresentationMode = settings.StartupPresentationMode;
        await RefreshModulesAsync();

        var succeeded = 0; var skipped = 0; var failed = 0;
        foreach (var authorization in settings.StartupModules)
        {
            var module = Modules.FirstOrDefault(item => item.Id == authorization.ModuleId);
            if (module is null || !await fingerprintService.MatchesAsync(module.Module, authorization))
            {
                skipped++;
                continue;
            }
            try
            {
                await runtimeManager.LoadAsync(module.Id, applicationPaths.ModuleDataDirectory);
                if (authorization.ActivateOnStartup) await runtimeManager.ActivateAsync(module.Id);
                module.UpdateRuntimeState(runtimeManager.GetRecord(module.Id));
                succeeded++;
            }
            catch (Exception exception)
            {
                module.UpdateRuntimeState(runtimeManager.GetRecord(module.Id));
                module.RuntimeError = exception.Message;
                failed++;
            }
        }
        UpdateStatistics();
        if (settings.StartupModules.Count > 0)
            StatusMessage = localization.GetString("startup.restoreSummary", succeeded, skipped, failed);
    }

    [RelayCommand]
    private async Task ToggleModuleStartupAsync(DiscoveredModuleViewModel module)
    {
        if (!module.CanChangeStartupAuthorization) return;
        var desired = module.IsStartupEnabled;
        module.IsStartupAuthorizationBusy = true;
        try
        {
            if (desired)
            {
                var authorization = await fingerprintService.CreateAuthorizationAsync(module.Module);
                await settingsService.UpdateAsync(settings =>
                {
                    settings.StartupModules.RemoveAll(item => item.ModuleId == module.Id);
                    settings.StartupModules.Add(authorization);
                });
                module.StartupAuthorizationMessage = localization.GetString("startup.moduleEnabled");
            }
            else
            {
                await settingsService.UpdateAsync(settings =>
                    settings.StartupModules.RemoveAll(item => item.ModuleId == module.Id));
                module.StartupAuthorizationMessage = localization.GetString("startup.moduleDisabled");
            }
        }
        catch (Exception exception)
        {
            module.IsStartupEnabled = !desired;
            module.StartupAuthorizationMessage = exception.Message;
        }
        finally { module.IsStartupAuthorizationBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyLaunchAtLoginAsync()
    {
        if (IsStartupSettingsBusy) return;
        IsStartupSettingsBusy = true;
        var desired = LaunchAtLogin;
        try
        {
            await startupRegistrationService.SetEnabledAsync(desired);
            var state = await startupRegistrationService.GetStateAsync();
            LaunchAtLogin = state.MatchesCurrentExecutable;
            StartupSettingsMessage = localization.GetString(LaunchAtLogin ? "startup.registrationEnabled" : "startup.registrationDisabled");
        }
        catch (Exception exception)
        {
            LaunchAtLogin = (await startupRegistrationService.GetStateAsync()).MatchesCurrentExecutable;
            StartupSettingsMessage = exception.Message;
        }
        finally { IsStartupSettingsBusy = false; }
    }

    partial void OnSelectedStartupPresentationModeChanged(StartupPresentationMode value)
    {
        if (!_initialized) return;
        _ = settingsService.UpdateAsync(settings => settings.StartupPresentationMode = value);
    }

    [RelayCommand]
    private async Task ImportModuleAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = localization.GetString("modules.importDialogTitle"),
            Filter = localization.GetString("modules.importFileFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var moduleId = await modulePackageImporter.ImportAsync(
                dialog.FileName,
                applicationPaths.UserModulesDirectory,
                Modules.Select(module => module.Id).ToArray());
            await RefreshModulesAsync();
            var importedModule = Modules.FirstOrDefault(
                module => module.Id == moduleId);
            SelectedModule = importedModule;
            SelectedNavigationKey = "Modules";
            StatusMessage = localization.GetString(
                "status.moduleImportedNextStep",
                importedModule?.DisplayName ?? moduleId);
        }
        catch (Exception exception)
        {
            StatusMessage = localization.GetString(
                "status.moduleImportFailed",
                exception.Message);
        }
    }
}
