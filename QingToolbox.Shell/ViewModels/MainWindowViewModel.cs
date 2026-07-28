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
using QingToolbox.Shell.Startup;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Updates;
using QingToolbox.Shell.Views;
using QingToolbox.Shell.WebShell;

namespace QingToolbox.Shell.ViewModels;

public sealed partial class MainWindowViewModel(
    ModuleDiscoveryCoordinator discoveryCoordinator,
    ModuleRegistry registry,
    ModuleRuntimeManager runtimeManager,
    ModuleWindowManager moduleWindowManager,
    ModulePackageImporter modulePackageImporter,
    ApplicationPaths applicationPaths,
    LocalizationManager localizationManager,
    UserSettingsService settingsService,
    WindowsStartupRegistrationService startupRegistrationService,
    ModuleStartupFingerprintService fingerprintService,
    ILocalizationService localization,
    ApplicationExecutionEnvironment executionEnvironment,
    ModuleUpdateCheckCoordinator moduleUpdateCoordinator,
    ModulePackageDownloadCoordinator modulePackageDownloadCoordinator,
    TimeProvider timeProvider,
    StartupHealthJournal startupHealthJournal,
    IModuleExecutionReadinessGate executionGate,
    SessionLogService sessionLog,
    ModuleUpdateRuntimeCoordinator updateRuntimeCoordinator,
    ModuleProcessBroker moduleProcessBroker) : ObservableObject
{
    public void ApplyNotificationAvailability(NotificationAvailabilityChangedEventArgs change)
    {
        if (change.Available && change.RecoveredAfterExplorerRestart)
            StartupSettingsMessage = localization.GetString("startup.notificationRecovered");
        else if (!change.Available)
            StartupSettingsMessage = localization.GetString("startup.notificationRetryExhausted");
    }
    public async Task ReconcileStartupRegistrationAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        await startupRegistrationService.ReconcileAsync(settings, cancellationToken);
        ApplyRegistrationHealth(await startupRegistrationService.GetStateAsync(cancellationToken));
    }

    private bool _initialized;
    private long _appliedDiscoveryGeneration;
    private bool _suppressPresentationSave;
    private int _presentationSaveVersion;
    private readonly SemaphoreSlim _presentationSaveGate = new(1, 1);
    private readonly SemaphoreSlim _closeBehaviorSaveGate = new(1, 1);
    private readonly SemaphoreSlim _languageChangeGate = new(1, 1);
    private readonly CancellationTokenSource _updateCancellation = new();
    private CancellationTokenSource? _automaticUpdateDelay;
    private readonly Dictionary<string, (string Version, ModuleUpdateResult Result)> _updateResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ModulePackageDownloadIdentity Identity, ModulePackageDownloadStatus Status)> _downloadResults = new(StringComparer.Ordinal);
    private bool _moduleUpdateUsedStaleCache;
    private StartupRegistrationState? _lastStartupRegistrationState;
    private DateTimeOffset? _moduleUpdateLastCheckedAt;
    private StartupPresentationMode _persistedStartupPresentationMode;
    private bool _suppressCloseBehaviorSave;
    private bool _suppressLanguageSelectionChange;
    private MainWindowCloseBehavior _persistedCloseBehavior = MainWindowCloseBehavior.Ask;
    private int _closeBehaviorSaveVersion;
    private bool _logSettingInitialized;
    private bool _processExitSubscribed;
    [ObservableProperty] private bool _isCheckingModuleUpdates;
    [ObservableProperty] private int _moduleUpdateCount;
    [ObservableProperty] private string _moduleUpdateSummary = string.Empty;
    [ObservableProperty] private string _moduleUpdateLastChecked = string.Empty;
    public bool CanCheckModuleUpdates => !executionEnvironment.IsModuleTest && !IsCheckingModuleUpdates;
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
    [ObservableProperty] private MainWindowCloseBehavior _selectedMainWindowCloseBehavior = MainWindowCloseBehavior.Ask;
    [ObservableProperty] private bool _isCloseBehaviorBusy;
    [ObservableProperty] private string _closeBehaviorMessage = string.Empty;
    [ObservableProperty] private int _startupAuthorizationCount;
    [ObservableProperty] private int _missingStartupAuthorizationCount;
    [ObservableProperty] private string _startupBackendDisplay = "—";
    [ObservableProperty] private string _startupHealthDisplay = "—";
    [ObservableProperty] private string _startupLastAttemptDisplay = "—";
    [ObservableProperty] private string _startupVisibleDurationDisplay = "—";
    [ObservableProperty] private string _startupReadyDurationDisplay = "—";
    [ObservableProperty] private string _startupFailureDisplay = "—";

    [ObservableProperty] private bool _canTestStartup;
    [ObservableProperty] private bool _canRepairStartup;
    [ObservableProperty] private bool _showLogsInSidebar = executionEnvironment.IsDevelopment;

    public IReadOnlyList<StartupPresentationMode> StartupPresentationModes { get; } = Enum.GetValues<StartupPresentationMode>();
    public string StartupAuthorizationSummary => localization.GetString("startup.authorizationSummary", StartupAuthorizationCount);
    public string MissingStartupAuthorizationSummary => localization.GetString("startup.missingSummary", MissingStartupAuthorizationCount);

    public string Title => executionEnvironment.DisplayName;
    public bool CanConfigureWindowsStartup => executionEnvironment.AllowWindowsStartupRegistration && !IsStartupSettingsBusy;
    public string VersionDisplay =>
        typeof(MainWindowViewModel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.2.0-alpha";
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
    public bool IsLogsSelected => SelectedNavigationKey == "Logs";
    public bool IsSettingsSelected => SelectedNavigationKey == "Settings";
    public string PageTitle => SelectedNavigationKey switch
    {
        "Modules" => localization.GetString("modules.title"),
        "Running" => localization.GetString("running.title"),
        "Logs" => localization.GetString("logs.title"),
        "Settings" => localization.GetString("settings.title"),
        _ => localization.GetString("app.name")
    };
    public string PageSubtitle => SelectedNavigationKey switch
    {
        "Modules" => localization.GetString("modules.subtitle"),
        "Running" => localization.GetString("running.subtitle"),
        "Logs" => localization.GetString("logs.subtitle"),
        "Settings" => localization.GetString("settings.subtitle"),
        _ => localization.GetString("app.subtitle")
    };

    public ObservableCollection<DiscoveredModuleViewModel> Modules { get; } = [];
    public ObservableCollection<DiscoveredModuleViewModel> RunningModules { get; } = [];
    public ObservableCollection<SessionLogEntry> LogEntries => sessionLog.Entries;
    public string CurrentLogPath => sessionLog.CurrentLogPath;
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
        if (key == "Logs" && !ShowLogsInSidebar) return;
        SelectedNavigationKey = key;
    }

    public void InitializeLogSettings(UserSettings settings)
    {
        ShowLogsInSidebar = settings.ShowLogsInSidebar ?? executionEnvironment.IsDevelopment;
        _logSettingInitialized = true;
        sessionLog.Information("Settings", $"Log navigation visibility initialized: {ShowLogsInSidebar}.");
    }

    [RelayCommand]
    private void OpenLogsDirectory()
    {
        try
        {
            Directory.CreateDirectory(sessionLog.LogsDirectory);
            Process.Start(new ProcessStartInfo(sessionLog.LogsDirectory) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception)
        {
            sessionLog.Error("Logs", "The logs directory could not be opened.", exception);
        }
    }

    [RelayCommand]
    private void ClearVisibleLogs() => sessionLog.ClearVisible();

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
    private void OpenModuleDirectory(DiscoveredModuleViewModel? module)
    {
        if (module is null) return;
        try
        {
            var directory = Path.GetFullPath(module.ModuleDirectory);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
            StatusMessage = localization.GetString("status.moduleDirectoryOpened", module.DisplayName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or ArgumentException or NotSupportedException)
        {
            StatusMessage = localization.GetString("status.moduleDirectoryOpenFailed", module.DisplayName);
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
        OnPropertyChanged(nameof(IsLogsSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(PinLabel));
    }

    partial void OnSelectedLanguageCodeChanged(string value)
    {
        if (!_suppressLanguageSelectionChange)
            _ = ChangeLanguageFromNativeAsync(value);
    }

    private async Task ChangeLanguageFromNativeAsync(string languageCode)
    {
        try
        {
            if (await ApplyLanguageSelectionAsync(languageCode, CancellationToken.None) !=
                WebSettingsMutationResult.Succeeded)
                RestoreSelectedLanguageCode();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Could not change QingToolbox language: {exception.GetType().Name}");
            RestoreSelectedLanguageCode();
        }
    }

    public Task<WebSettingsMutationResult> SetLanguageFromWebAsync(
        string languageCode,
        CancellationToken cancellationToken) =>
        ApplyLanguageSelectionAsync(languageCode, cancellationToken);

    private async Task<WebSettingsMutationResult> ApplyLanguageSelectionAsync(
        string languageCode,
        CancellationToken cancellationToken)
    {
        if (!localizationManager.SupportedLanguages.Any(option =>
                string.Equals(option.Code, languageCode, StringComparison.Ordinal)))
            return WebSettingsMutationResult.Failed;

        await _languageChangeGate.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await localizationManager.SetLanguageAsync(languageCode, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Could not persist QingToolbox language: {exception.GetType().Name}");
                RestoreSelectedLanguageCode();
                return WebSettingsMutationResult.Failed;
            }

            ApplyLocalizedPresentation(languageCode);
            return localizationManager.ConfiguredLanguageCode == languageCode &&
                   SelectedLanguageCode == languageCode
                ? WebSettingsMutationResult.Succeeded
                : WebSettingsMutationResult.Failed;
        }
        finally
        {
            _languageChangeGate.Release();
        }
    }

    private void ApplyLocalizedPresentation(string languageCode)
    {
        foreach (var module in Modules)
        {
            module.RefreshLocalization();
        }
        moduleWindowManager.RefreshOpenWindowLocalization(moduleId =>
            Modules.FirstOrDefault(module => module.Id == moduleId)?.DisplayName);

        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(PinLabel));
        OnPropertyChanged(nameof(StartupAuthorizationSummary));
        OnPropertyChanged(nameof(MissingStartupAuthorizationSummary));
        RefreshLanguageOptionLabels();
        if (!string.IsNullOrEmpty(ModuleUpdateLastChecked))
        {
            ModuleUpdateSummary = localization.GetString(
                _moduleUpdateUsedStaleCache ? "moduleUpdate.completedStale" : "moduleUpdate.completed",
                ModuleUpdateCount);
            ModuleUpdateLastChecked = localization.GetString("moduleUpdate.lastChecked", _moduleUpdateLastCheckedAt!.Value.LocalDateTime.ToString("g"));
        }
        var option = LanguageOptions.FirstOrDefault(
            item => item.Code == languageCode);
        StatusMessage = localization.GetString(
            "status.languageChanged",
            option?.DisplayText ?? languageCode);
        SetSelectedLanguageCode(languageCode);
    }

    private void RestoreSelectedLanguageCode() =>
        SetSelectedLanguageCode(localizationManager.ConfiguredLanguageCode);

    private void SetSelectedLanguageCode(string languageCode)
    {
        if (SelectedLanguageCode == languageCode) return;
        _suppressLanguageSelectionChange = true;
        try { SelectedLanguageCode = languageCode; }
        finally { _suppressLanguageSelectionChange = false; }
    }

    partial void OnIsCheckingModuleUpdatesChanged(bool value) => OnPropertyChanged(nameof(CanCheckModuleUpdates));

    [RelayCommand(CanExecute = nameof(CanCheckModuleUpdates))]
    private async Task CheckModuleUpdatesAsync()
    {
        if (IsCheckingModuleUpdates) return;
        _automaticUpdateDelay?.Cancel();
        await RunModuleUpdateCheckAsync(true);
    }

    private async Task RunModuleUpdateCheckAsync(bool manual)
    {
        if (IsCheckingModuleUpdates) return;
        var snapshot = Modules.Select(x => new InstalledModuleVersion(x.Id, x.Version)).ToArray();
        if (snapshot.Length == 0)
        {
            ModuleUpdateSummary = localization.GetString("moduleUpdate.noModules");
            return;
        }
        IsCheckingModuleUpdates = true;
        CheckModuleUpdatesCommand.NotifyCanExecuteChanged();
        ModuleUpdateSummary = localization.GetString("moduleUpdate.checking");
        var previous = Modules.ToDictionary(x => x.Id, x => x.UpdateResult, StringComparer.Ordinal);
        foreach (var module in Modules) module.UpdateResult = new(module.Id, ModuleUpdateStatus.Checking);
        try
        {
            var batch = await moduleUpdateCoordinator.CheckAsync(
                new(snapshot, manual, timeProvider.GetUtcNow()), _updateCancellation.Token);
            if (batch.Disposition == ModuleUpdateBatchDisposition.DuplicateSuppressed)
            {
                foreach (var module in Modules)
                    if (previous.TryGetValue(module.Id, out var prior)) module.UpdateResult = prior;
                return;
            }
            ApplyUpdateResults(batch);
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested) { }
        catch
        {
            foreach (var module in Modules)
                module.UpdateResult = previous.GetValueOrDefault(module.Id) is { Status: not ModuleUpdateStatus.Checking } prior
                    ? prior : new(module.Id, ModuleUpdateStatus.SourceUnavailable);
            ModuleUpdateSummary = localization.GetString("moduleUpdate.unexpectedFailure");
        }
        finally { IsCheckingModuleUpdates = false; CheckModuleUpdatesCommand.NotifyCanExecuteChanged(); }
    }

    private void ApplyUpdateResults(ModuleUpdateBatchResult batch)
    {
        var currentIds = Modules.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _updateResults.Keys.Where(x => !currentIds.Contains(x)).ToArray()) _updateResults.Remove(removed);
        var currentVersions = Modules.ToDictionary(module => module.Id, module => module.Version, StringComparer.Ordinal);
        var application = ModuleUpdateBatchApplicator.Apply(currentVersions, batch);
        if (application.ResultsOutdated)
        {
            ModuleUpdateSummary = localization.GetString("moduleUpdate.resultsOutdated");
            return;
        }
        foreach (var module in Modules)
            if (application.AppliedResults.TryGetValue(module.Id, out var bound))
            {
                module.UpdateResult = bound.Result;
                _updateResults[module.Id] = (bound.LocalVersion, bound.Result);
            }
        ModuleUpdateCount = application.UpdateCount;
        var stale = application.UsedStaleCache;
        _moduleUpdateUsedStaleCache = stale;
        _moduleUpdateLastCheckedAt = batch.CompletedAt;
        ModuleUpdateLastChecked = localization.GetString("moduleUpdate.lastChecked", _moduleUpdateLastCheckedAt.Value.LocalDateTime.ToString("g"));
        ModuleUpdateSummary = localization.GetString(stale ? "moduleUpdate.completedStale" : "moduleUpdate.completed", ModuleUpdateCount);
    }

    private async Task RunAutomaticUpdateCheckObservedAsync()
    {
        try
        {
            _automaticUpdateDelay = CancellationTokenSource.CreateLinkedTokenSource(_updateCancellation.Token);
            await Task.Delay(TimeSpan.FromSeconds(4), _automaticUpdateDelay.Token);
            await Application.Current.Dispatcher.InvokeAsync(() => RunModuleUpdateCheckAsync(false)).Task.Unwrap();
        }
        catch (OperationCanceledException) when (_updateCancellation.IsCancellationRequested || _automaticUpdateDelay?.IsCancellationRequested == true) { }
        catch (Exception exception) { Debug.WriteLine($"Background module update check failed: {exception.GetType().Name}"); }
    }

    public void StopUpdateChecks() { _automaticUpdateDelay?.Cancel(); _updateCancellation.Cancel(); moduleUpdateCoordinator.Cancel(); modulePackageDownloadCoordinator.Cancel(); }

    [RelayCommand]
    private async Task DownloadModulePackageAsync(DiscoveredModuleViewModel module)
    {
        if (!module.CanDownloadUpdate || module.UpdateResult.SelectedRelease is not { } release) return;
        var request = new ModulePackageDownloadRequest(module.Id, module.Version, release.Version, release.Package);
        var identity = ModulePackageDownloadIdentity.From(request);
        module.DownloadStatus = ModulePackageDownloadStatus.ConfirmingMetadata;
        module.DownloadBytesReceived = 0; module.DownloadExpectedBytes = release.Package.Size;
        var progress = new Progress<ModulePackageDownloadProgress>(value =>
        {
            var current = Modules.FirstOrDefault(item => item.Id == identity.ModuleId);
            if (current is null || !ModulePackageUiBinding.Matches(identity, current.Id, current.Version, current.UpdateResult)) return;
            current.DownloadStatus = value.BytesReceived == value.ExpectedBytes
                ? ModulePackageDownloadStatus.Verifying : ModulePackageDownloadStatus.Downloading;
            current.DownloadBytesReceived = value.BytesReceived;
            current.DownloadExpectedBytes = value.ExpectedBytes;
        });
        ModulePackageDownloadResult result;
        try { result = await modulePackageDownloadCoordinator.DownloadAsync(request, progress); }
        catch (Exception exception) { Debug.WriteLine($"Module package download failed: {exception.GetType().Name}"); result = new(ModulePackageDownloadStatus.Failed); }
        var currentModule = Modules.FirstOrDefault(item => item.Id == identity.ModuleId);
        if (currentModule is null || currentModule.Version != identity.LocalVersion) return;
        if (result.LatestUpdateResult is { } latest &&
            result.Status is ModulePackageDownloadStatus.MetadataChanged or ModulePackageDownloadStatus.MetadataStale)
        {
            currentModule.UpdateResult = latest;
            _updateResults[currentModule.Id] = (currentModule.Version, latest);
        }
        else if (!ModulePackageUiBinding.Matches(identity, currentModule.Id, currentModule.Version, currentModule.UpdateResult)) return;
        currentModule.DownloadStatus = result.Status;
        _downloadResults[currentModule.Id] = (identity, result.Status);
    }

    [RelayCommand]
    private void CancelModulePackageDownload(DiscoveredModuleViewModel module)
    {
        if (module.UpdateResult.SelectedRelease is not { } release) return;
        modulePackageDownloadCoordinator.Cancel(new(module.Id, module.Version, release.Version, release.Package));
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

    partial void OnShowLogsInSidebarChanged(bool value)
    {
        if (!value && SelectedNavigationKey == "Logs") SelectedNavigationKey = "Settings";
        if (_logSettingInitialized && !_suppressLogVisibilitySave) _ = SaveLogVisibilityAsync(value);
    }

    private readonly SemaphoreSlim _webLogVisibilityGate = new(1, 1);
    private bool _suppressLogVisibilitySave;

    public async Task SetShowLogsInSidebarAsync(bool value, CancellationToken cancellationToken = default)
    {
        await _webLogVisibilityGate.WaitAsync(cancellationToken);
        try
        {
            if (ShowLogsInSidebar == value) return;
            await settingsService.UpdateAsync(settings => settings.ShowLogsInSidebar = value, cancellationToken);
            _suppressLogVisibilitySave = true;
            try { ShowLogsInSidebar = value; }
            finally { _suppressLogVisibilitySave = false; }
            sessionLog.Information("Settings", $"Log navigation visibility changed by Web Settings: {value}.");
        }
        finally { _webLogVisibilityGate.Release(); }
    }

    private async Task SaveLogVisibilityAsync(bool value)
    {
        try
        {
            await settingsService.UpdateAsync(settings => settings.ShowLogsInSidebar = value);
            sessionLog.Information("Settings", $"Log navigation visibility changed: {value}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            sessionLog.Error("Settings", "Log navigation visibility could not be saved.", exception);
        }
    }

    [RelayCommand]
    private async Task RefreshModulesAsync(CancellationToken cancellationToken = default)
    {
        try { await RefreshModulesCoreAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ModuleDiscoveryException)
        {
            UpdateStatistics();
            StatusMessage = localization.GetString("status.scanFailed", exception.Message);
        }
    }

    private async Task RefreshModulesCoreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            sessionLog.Information("Discovery", "Module discovery started.");
            IsScanning = true;
            foreach (var active in Modules.Where(module => module.IsDownloadActive && module.UpdateResult.SelectedRelease is not null))
            {
                var release = active.UpdateResult.SelectedRelease!;
                modulePackageDownloadCoordinator.Cancel(new(active.Id, active.Version, release.Version, release.Package));
            }
            StatusMessage = localization.GetString(
                "status.scanningModules");

            var discovery = await discoveryCoordinator.DiscoverAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (discovery.Generation <= _appliedDiscoveryGeneration) return;
            var discoveredModules = discovery.Modules;
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

            cancellationToken.ThrowIfCancellationRequested();
            var settings = discovery.Settings;
            var authorizations = settings.StartupModules.ToDictionary(item => item.ModuleId, StringComparer.Ordinal);
            var refreshedModules = new List<DiscoveredModuleViewModel>();
            foreach (var module in discoveredModules)
            {
                var moduleViewModel = new DiscoveredModuleViewModel(
                    module,
                    localization,
                    localizationDiagnosticsByModuleId.GetValueOrDefault(
                        module.Manifest.Id),
                    executionEnvironment.IsModuleTest);
                moduleViewModel.IsUserInstalled = IsDirectChildOf(
                    module.ModuleDirectory,
                    applicationPaths.UserModulesDirectory);
                if (_updateResults.TryGetValue(module.Manifest.Id, out var prior) && prior.Version == module.Manifest.Version)
                    moduleViewModel.UpdateResult = prior.Result;
                else if (executionEnvironment.IsModuleTest)
                    moduleViewModel.UpdateResult = new(module.Manifest.Id, ModuleUpdateStatus.DisabledByEnvironment);
                if (_updateResults.TryGetValue(module.Manifest.Id, out prior) && prior.Version != module.Manifest.Version)
                    _updateResults.Remove(module.Manifest.Id);
                if (moduleViewModel.UpdateResult.SelectedRelease is not null &&
                    _downloadResults.TryGetValue(module.Manifest.Id, out var download) &&
                    ModulePackageUiBinding.Matches(download.Identity, module.Manifest.Id, module.Manifest.Version, moduleViewModel.UpdateResult))
                    moduleViewModel.DownloadStatus = download.Status;
                else
                    _downloadResults.Remove(module.Manifest.Id);
                RefreshRuntimeProjection(moduleViewModel);
                moduleViewModel.UpdateExecutionReadiness(
                    executionGate.GetReadiness(module.Manifest.Id));
                if (authorizations.ContainsKey(module.Manifest.Id) &&
                    discovery.Authorizations.TryGetValue(module.Manifest.Id, out var evaluation))
                {
                    moduleViewModel.IsStartupEnabled = evaluation.FingerprintMatches;
                    var unavailable = evaluation.FailureDiagnostic?.StartsWith("startup.moduleUnavailable", StringComparison.Ordinal) == true;
                    moduleViewModel.StartupAuthorizationState = unavailable
                        ? StartupAuthorizationState.Unavailable
                        : moduleViewModel.IsStartupEnabled ? StartupAuthorizationState.Enabled
                        : StartupAuthorizationState.ChangedNeedsConfirmation;
                    moduleViewModel.StartupAuthorizationMessage = localization.GetString(unavailable
                        ? "startup.moduleUnavailable"
                        : moduleViewModel.IsStartupEnabled ? "startup.moduleEnabled" : "startup.moduleChanged");
                }
                refreshedModules.Add(moduleViewModel);
            }

            var selectedModuleId = SelectedModule?.Id;
            Modules.Clear();
            foreach (var module in refreshedModules)
            {
                Modules.Add(module);
            }
            _appliedDiscoveryGeneration = discovery.Generation;

            SelectedModule = Modules.FirstOrDefault(
                module => module.Id == selectedModuleId) ?? Modules.FirstOrDefault();
            foreach (var removed in _updateResults.Keys.Where(id => Modules.All(module => module.Id != id)).ToArray())
                _updateResults.Remove(removed);
            foreach (var removed in _downloadResults.Keys.Where(id => Modules.All(module => module.Id != id)).ToArray())
                _downloadResults.Remove(removed);

            StartupAuthorizationCount = settings.StartupModules.Count;
            MissingStartupAuthorizationCount = settings.StartupModules.Count(
                authorization => Modules.All(module => module.Id != authorization.ModuleId));

            UpdateStatistics();
            StatusMessage = localization.GetString(
                "status.modulesFound",
                Modules.Count);
            sessionLog.Information("Discovery", $"Module discovery completed. Count={Modules.Count}; Valid={ValidModuleCount}; Failed={FailedModuleCount}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            IsScanning = discoveryCoordinator.IsRunning;
        }
    }

    [RelayCommand]
    private Task LoadModuleAsync(string moduleId) => LoadModuleOperationAsync(moduleId, CancellationToken.None, false);

    public Task<WebModuleLifecycleResult> LoadModuleFromWebAsync(string moduleId, CancellationToken cancellationToken) =>
        LoadModuleOperationAsync(moduleId, cancellationToken, true);

    private Task<WebModuleLifecycleResult> LoadModuleOperationAsync(string moduleId, CancellationToken cancellationToken, bool validateAvailability)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsOutOfProcessWpf(moduleId))
            return ExecuteLifecycleAsync(moduleId, "load", async () =>
            {
                if (!await updateRuntimeCoordinator.RestorePreviousRuntimeStateAsync(moduleId,
                        new(false, false, true, false), CancellationToken.None))
                    throw new ModuleRuntimeException("The module worker could not be started.");
            }, validateAvailability);
        return ExecuteLifecycleAsync(
            moduleId,
            "load",
            () => runtimeManager.LoadAsync(
                moduleId,
                applicationPaths.ModuleDataDirectory), validateAvailability);
    }

    [RelayCommand]
    private Task ActivateModuleAsync(string moduleId) => ActivateModuleOperationAsync(moduleId, CancellationToken.None, false);

    public Task<WebModuleLifecycleResult> ActivateModuleFromWebAsync(string moduleId, CancellationToken cancellationToken) =>
        ActivateModuleOperationAsync(moduleId, cancellationToken, true);

    private Task<WebModuleLifecycleResult> ActivateModuleOperationAsync(string moduleId, CancellationToken cancellationToken, bool validateAvailability)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsOutOfProcessWpf(moduleId))
            return ExecuteLifecycleAsync(moduleId, "activate", async () =>
            {
                if (moduleProcessBroker.GetState(moduleId) is null)
                    await LoadOutOfProcessAsync(moduleId);
                if (!await moduleProcessBroker.CommandAsync(moduleId, "Activate", CancellationToken.None))
                    throw new ModuleRuntimeException("The module worker could not be activated.");
            }, validateAvailability);
        return ExecuteLifecycleAsync(
            moduleId,
            "activate",
            () => runtimeManager.ActivateAsync(moduleId), validateAvailability);
    }

    [RelayCommand]
    private Task DeactivateModuleAsync(string moduleId) => DeactivateModuleOperationAsync(moduleId, CancellationToken.None, false);

    public Task<WebModuleLifecycleResult> DeactivateModuleFromWebAsync(string moduleId, CancellationToken cancellationToken) =>
        DeactivateModuleOperationAsync(moduleId, cancellationToken, true);

    private Task<WebModuleLifecycleResult> DeactivateModuleOperationAsync(string moduleId, CancellationToken cancellationToken, bool validateAvailability)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsOutOfProcessWpf(moduleId))
            return ExecuteLifecycleAsync(moduleId, "deactivate", async () =>
            {
                if (!await updateRuntimeCoordinator.DeactivateAsync(moduleId, cancellationToken))
                    throw new ModuleRuntimeException("The module worker could not be deactivated.");
            }, validateAvailability);
        return ExecuteLifecycleAsync(
            moduleId,
            "deactivate",
            () => runtimeManager.DeactivateAsync(moduleId), validateAvailability);
    }

    [RelayCommand]
    private Task UnloadModuleAsync(string moduleId) => UnloadModuleOperationAsync(moduleId, CancellationToken.None, false);

    public Task<WebModuleLifecycleResult> UnloadModuleFromWebAsync(string moduleId, CancellationToken cancellationToken) =>
        UnloadModuleOperationAsync(moduleId, cancellationToken, true);

    private Task<WebModuleLifecycleResult> UnloadModuleOperationAsync(string moduleId, CancellationToken cancellationToken, bool validateAvailability)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsOutOfProcessWpf(moduleId))
            return ExecuteLifecycleAsync(moduleId, "unload", async () =>
            {
                if (!await updateRuntimeCoordinator.UnloadAsync(moduleId, cancellationToken) ||
                    !await updateRuntimeCoordinator.VerifyUnloadedAsync(moduleId, cancellationToken))
                    throw new ModuleRuntimeException("The module worker did not exit.");
            }, validateAvailability);
        return ExecuteLifecycleAsync(
            moduleId,
            "unload",
            async () =>
            {
                moduleWindowManager.CloseWindow(moduleId);
                await runtimeManager.UnloadAsync(moduleId);
            }, validateAvailability);
    }

    [RelayCommand]
    private Task OpenModuleAsync(string moduleId) => OpenModuleOperationAsync(moduleId, CancellationToken.None);

    public Task<WebModuleLifecycleResult> OpenModuleFromWebAsync(string moduleId, CancellationToken cancellationToken) =>
        OpenModuleOperationAsync(moduleId, cancellationToken);

    private async Task<WebModuleLifecycleResult> OpenModuleOperationAsync(string moduleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var moduleViewModel = Modules.FirstOrDefault(
            module => string.Equals(module.Id, moduleId, StringComparison.Ordinal));

        if (moduleViewModel is null) return WebModuleLifecycleResult.NotFound;
        if (moduleViewModel.IsBusy) return WebModuleLifecycleResult.Busy;

        if (!moduleViewModel.CanOpen)
        {
            StatusMessage = moduleViewModel.IsExecutionBlocked
                ? localization.GetString("status.moduleBlockedByRecovery", moduleViewModel.DisplayName)
                : localization.GetString("status.moduleLoadBeforeOpen", moduleViewModel.DisplayName);
            return moduleViewModel.IsExecutionBlocked
                ? WebModuleLifecycleResult.ExecutionBlocked
                : WebModuleLifecycleResult.Unavailable;
        }

        moduleViewModel.IsBusy = true;
        moduleViewModel.RuntimeError = string.Empty;

        try
        {
            await using var executionLease = await executionGate.EnterExecutionAsync(moduleId);
            if (IsOutOfProcessWpf(moduleId))
            {
                if (!await moduleProcessBroker.CommandAsync(moduleId, "OpenWindow", CancellationToken.None))
                    throw new ModuleRuntimeException("The module window could not be opened.");
                moduleViewModel.UpdateOutOfProcessRuntimeState(moduleProcessBroker.GetState(moduleId));
                StatusMessage = localization.GetString("status.moduleOpened", moduleViewModel.DisplayName);
                return WebModuleLifecycleResult.Succeeded;
            }
            if (moduleWindowManager.IsWindowOpen(moduleId))
            {
                moduleWindowManager.ActivateWindow(moduleId);
                StatusMessage = localization.GetString(
                    "status.moduleFocused",
                    moduleViewModel.DisplayName);
                return WebModuleLifecycleResult.Succeeded;
            }

            var view = runtimeManager.CreateView(moduleId);
            RefreshRuntimeProjection(moduleViewModel);

            if (view is null)
            {
                StatusMessage = localization.GetString(
                    "status.moduleNoView",
                    moduleViewModel.DisplayName);
                return WebModuleLifecycleResult.Unavailable;
            }

            moduleWindowManager.OpenWindow(
                moduleId,
                moduleViewModel.Name,
                view,
                Application.Current.MainWindow);
            StatusMessage = localization.GetString(
                "status.moduleOpened",
                moduleViewModel.DisplayName);
            return WebModuleLifecycleResult.Succeeded;
        }
        catch (ModuleExecutionBlockedException exception)
        {
            moduleViewModel.UpdateExecutionReadiness(executionGate.GetReadiness(moduleId));
            StatusMessage = localization.GetString(
                "status.moduleBlockedByRecovery",
                moduleViewModel.DisplayName);
            sessionLog.Warning("ModuleRecovery",
                $"Module execution blocked by recovery; module={moduleId}; state={exception.Status}.");
            return WebModuleLifecycleResult.ExecutionBlocked;
        }
        catch (Exception exception)
        {
            RefreshRuntimeProjection(moduleViewModel);
            if (!moduleViewModel.HasRuntimeError)
            {
                moduleViewModel.RuntimeError = exception.Message;
            }

            StatusMessage = localization.GetString(
                "status.moduleOpenFailed",
                moduleViewModel.DisplayName,
                exception.Message);
            return WebModuleLifecycleResult.Failed;
        }
        finally
        {
            moduleViewModel.IsBusy = false;
            UpdateStatistics();
        }

    }

    public void CloseModuleWindows() => moduleWindowManager.CloseAll();

    private async Task<WebModuleLifecycleResult> ExecuteLifecycleAsync(
        string moduleId,
        string operation,
        Func<Task> action,
        bool validateAvailability = false)
    {
        var moduleViewModel = Modules.FirstOrDefault(
            module => string.Equals(module.Id, moduleId, StringComparison.Ordinal));

        if (moduleViewModel is null) return WebModuleLifecycleResult.NotFound;
        if (moduleViewModel.IsBusy) return WebModuleLifecycleResult.Busy;
        if (validateAvailability)
        {
            if (moduleViewModel.IsExecutionBlocked) return WebModuleLifecycleResult.ExecutionBlocked;
            if (operation == "load" && !moduleViewModel.CanLoad ||
                operation == "activate" && !moduleViewModel.CanActivate ||
                operation == "deactivate" && !moduleViewModel.CanDeactivate ||
                operation == "unload" && !moduleViewModel.CanUnload)
                return WebModuleLifecycleResult.Unavailable;
        }

        moduleViewModel.IsBusy = true;
        moduleViewModel.RuntimeError = string.Empty;
        var localizedOperation = localization.GetString(
            $"common.{operation}");
        StatusMessage = localization.GetString(
            "status.moduleOperationStarted",
            localizedOperation,
            moduleViewModel.DisplayName);
        sessionLog.Information("Module", $"{operation} started for module '{moduleId}'.");

        try
        {
            await using var executionLease = await executionGate.EnterExecutionAsync(moduleId);
            await action();
            if (IsOutOfProcessWpf(moduleId))
                moduleViewModel.UpdateOutOfProcessRuntimeState(moduleProcessBroker.GetState(moduleId));
            else
                RefreshRuntimeProjection(moduleViewModel);
            StatusMessage = localization.GetString(
                "status.moduleOperationCompleted",
                moduleViewModel.DisplayName,
                localizedOperation);
            sessionLog.Information("Module", $"{operation} completed for module '{moduleId}'.");
            return WebModuleLifecycleResult.Succeeded;
        }
        catch (ModuleExecutionBlockedException exception)
        {
            moduleViewModel.UpdateExecutionReadiness(executionGate.GetReadiness(moduleId));
            StatusMessage = localization.GetString(
                "status.moduleBlockedByRecovery",
                moduleViewModel.DisplayName);
            sessionLog.Warning("ModuleRecovery",
                $"Module execution blocked by recovery; module={moduleId}; state={exception.Status}.");
            return WebModuleLifecycleResult.ExecutionBlocked;
        }
        catch (Exception exception)
        {
            RefreshRuntimeProjection(moduleViewModel);
            if (!moduleViewModel.HasRuntimeError)
            {
                moduleViewModel.RuntimeError = exception.Message;
            }

            StatusMessage = localization.GetString(
                "status.moduleOperationFailed",
                localizedOperation,
                moduleViewModel.DisplayName,
                exception.Message);
            sessionLog.Error("Module", $"{operation} failed for module '{moduleId}'.", exception);
            return WebModuleLifecycleResult.Failed;
        }
        finally
        {
            moduleViewModel.IsBusy = false;
            UpdateStatistics();
        }
    }

    private bool IsOutOfProcessWpf(string moduleId)
    {
        var module = Modules.FirstOrDefault(item => item.Id == moduleId);
        return module is not null && ModuleRuntimeCapabilities.Resolve(module.Module.Manifest) is
            { RuntimeIsolation: ModuleRuntimeIsolation.OutOfProcess, UiKind: ModuleUiKind.Wpf };
    }

    private void RefreshRuntimeProjection(DiscoveredModuleViewModel module)
    {
        if (IsOutOfProcessWpf(module.Id))
            module.UpdateOutOfProcessRuntimeState(moduleProcessBroker.GetState(module.Id));
        else
            module.UpdateRuntimeState(runtimeManager.GetRecord(module.Id));
    }

    private void EnsureProcessExitSubscription()
    {
        if (_processExitSubscribed) return;
        _processExitSubscribed = true;
        moduleProcessBroker.ProcessExited += OnModuleProcessExited;
    }

    private void OnModuleProcessExited(object? sender, ModuleProcessExitedEventArgs args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.InvokeAsync(() =>
        {
            var module = Modules.FirstOrDefault(item => item.Id == args.ModuleId);
            if (module is null) return;
            RefreshRuntimeProjection(module);
            if (!args.Expected) module.RuntimeError = args.FailureCode;
            UpdateStatistics();
        });
    }

    private async Task LoadOutOfProcessAsync(string moduleId)
    {
        if (!await updateRuntimeCoordinator.RestorePreviousRuntimeStateAsync(moduleId,
                new(false, false, true, false), CancellationToken.None))
            throw new ModuleRuntimeException("The module worker could not be started.");
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

    [RelayCommand]
    private async Task RemoveModuleAsync(string moduleId)
    {
        var module = Modules.FirstOrDefault(item => item.Id == moduleId);
        if (module is null || !module.CanRemove) return;

        var dialog = new ModuleRemovalDialog(localization, module.DisplayName)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dialog.ShowDialog() != true) return;

        module.IsBusy = true;
        try
        {
            await using var executionLease = await executionGate.EnterExecutionAsync(moduleId);
            if (IsOutOfProcessWpf(moduleId))
            {
                if (!await moduleProcessBroker.CommandAsync(moduleId, "CloseWindow", CancellationToken.None) ||
                    !await moduleProcessBroker.CommandAsync(moduleId, "Deactivate", CancellationToken.None) ||
                    !await moduleProcessBroker.ShutdownAsync(moduleId, CancellationToken.None) ||
                    !moduleProcessBroker.VerifyExited(moduleId) || moduleProcessBroker.HasSession(moduleId))
                    throw new IOException("The module worker did not exit cleanly; removal was cancelled.");
            }
            else
            {
                moduleWindowManager.CloseWindow(moduleId);
                var record = runtimeManager.GetRecord(moduleId);
                if (record?.State.ToString() == "Running") await runtimeManager.DeactivateAsync(moduleId);
                record = runtimeManager.GetRecord(moduleId);
                if (record?.State.ToString() is "Loaded" or "Deactivated" or "Failed")
                    await runtimeManager.UnloadAsync(moduleId);
            }

            var removal = await ModuleProgramRemoval.DeleteAsync(
                moduleId, module.ModuleDirectory, applicationPaths.UserModulesDirectory, settingsService);

            if (removal.Status == ModuleProgramRemovalStatus.ProgramDeletionFailed)
            {
                RefreshRuntimeProjection(module);
                module.RuntimeError = removal.FailureCode ?? string.Empty;
                StatusMessage = localization.GetString(
                    "status.moduleRemoveFailed", module.DisplayName, removal.FailureCode ?? string.Empty);
                return;
            }

            SelectedModule = null;
            await RefreshModulesAsync();
            StatusMessage = removal.Status == ModuleProgramRemovalStatus.Completed
                ? localization.GetString("status.moduleRemoved", module.DisplayName)
                : localization.GetString("status.moduleRemoveAuthorizationFailed", module.DisplayName);
        }
        catch (ModuleExecutionBlockedException)
        {
            module.UpdateExecutionReadiness(executionGate.GetReadiness(moduleId));
            StatusMessage = localization.GetString(
                "status.moduleBlockedByRecovery",
                module.DisplayName);
        }
        catch (Exception exception)
        {
            RefreshRuntimeProjection(module);
            StatusMessage = localization.GetString("status.moduleRemoveFailed", module.DisplayName, exception.Message);
        }
        finally
        {
            module.IsBusy = false;
        }
    }

    private static bool IsDirectChildOf(string candidate, string parent)
        => ModuleProgramRemoval.IsDirectChildOf(candidate, parent);

    public async Task InitializeDiscoveryAsync(CancellationToken cancellationToken = default)
    {
        EnsureProcessExitSubscription();
        if (_initialized) return;
        await RefreshModulesCoreAsync(cancellationToken);
        _initialized = true;
        if (executionEnvironment.IsProduction) _ = RunAutomaticUpdateCheckObservedAsync();
    }

    public async Task InitializeStartupSettingsUiAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        var registration = await startupRegistrationService.GetStateAsync(cancellationToken);
        LaunchAtLogin = registration.ConfiguredByUser && registration.MatchesCurrentExecutable;
        StartupSettingsMessage = localization.GetString(registration.DiagnosticCode);
        ApplyRegistrationHealth(registration);
        await RefreshStartupJournalAsync(cancellationToken);
        if (!executionEnvironment.AllowWindowsStartupRegistration)
            StartupSettingsMessage = localization.GetString("startup.unavailableInSandbox");
        _suppressPresentationSave = true;
        SelectedStartupPresentationMode = settings.StartupPresentationMode;
        _persistedStartupPresentationMode = settings.StartupPresentationMode;
        _suppressPresentationSave = false;
        _suppressCloseBehaviorSave = true;
        SelectedMainWindowCloseBehavior = settings.MainWindowCloseBehavior;
        _persistedCloseBehavior = settings.MainWindowCloseBehavior;
        _suppressCloseBehaviorSave = false;
    }

    [RelayCommand]
    private async Task RefreshStartupHealthAsync()
    {
        if (IsStartupSettingsBusy) return;
        IsStartupSettingsBusy=true;
        try{var state=await startupRegistrationService.GetStateAsync();ApplyRegistrationHealth(state);await RefreshStartupJournalAsync();}
        finally{IsStartupSettingsBusy=false;}
    }

    [RelayCommand]
    private async Task RepairStartupAsync()
    {
        _ = await RepairStartupRegistrationCoreAsync(CancellationToken.None);
    }

    public Task<WebSettingsMutationResult> RepairStartupFromWebAsync(CancellationToken cancellationToken) =>
        RepairStartupRegistrationCoreAsync(cancellationToken);

    private async Task<WebSettingsMutationResult> RepairStartupRegistrationCoreAsync(CancellationToken cancellationToken)
    {
        if (!CanConfigureWindowsStartup || !CanRepairStartup) return WebSettingsMutationResult.Unavailable;
        IsStartupSettingsBusy = true;
        try
        {
            await startupRegistrationService.RepairAsync(cancellationToken);
            ApplyRegistrationSnapshot(await startupRegistrationService.GetSnapshotAsync(cancellationToken));
            return _lastStartupRegistrationState is { } confirmed && IsStartupRepairRequired(confirmed.Health)
                ? WebSettingsMutationResult.Failed : WebSettingsMutationResult.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            try { ApplyRegistrationSnapshot(await startupRegistrationService.GetSnapshotAsync(cancellationToken)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
            StartupSettingsMessage = localization.GetString("startup.registrationFailed");
            return WebSettingsMutationResult.Failed;
        }
        finally { IsStartupSettingsBusy = false; }
    }

    [RelayCommand]
    private async Task TestStartupAsync()
    {
        if(!CanConfigureWindowsStartup)return;IsStartupSettingsBusy=true;
        try
        {
            var result = await startupRegistrationService.RunTestAsync();
            StartupSettingsMessage = localization.GetString(result.DiagnosticCode);
            ApplyRegistrationSnapshot(await startupRegistrationService.GetSnapshotAsync());
        }
        catch{StartupSettingsMessage=localization.GetString("startup.testFailed");}
        finally{IsStartupSettingsBusy=false;}
    }

    [RelayCommand]
    private void OpenTaskScheduler()
    {
        if(!executionEnvironment.IsProduction)return;
        try{Process.Start(new ProcessStartInfo("taskschd.msc"){UseShellExecute=true});}catch{StartupSettingsMessage=localization.GetString("startup.schedulerOpenFailed");}
    }

    [RelayCommand]
    private void CopyStartupDiagnostics()
    {
        try{Clipboard.SetText($"Backend: {StartupBackendDisplay}\nHealth: {StartupHealthDisplay}\nLast attempt: {StartupLastAttemptDisplay}\nVisible: {StartupVisibleDurationDisplay}\nReady: {StartupReadyDurationDisplay}\nFailure: {StartupFailureDisplay}");StartupSettingsMessage=localization.GetString("startup.diagnosticsCopied");}catch{StartupSettingsMessage=localization.GetString("startup.diagnosticsCopyFailed");}
    }

    private void ApplyRegistrationHealth(StartupRegistrationState state)
    {
        _lastStartupRegistrationState = state;
        StartupBackendDisplay = localization.GetString(state.Backend switch
        {
            StartupRegistrationBackendKind.TaskScheduler => "startup.backendTaskScheduler",
            StartupRegistrationBackendKind.RegistryRun => "startup.backendRegistryRun",
            _ => "startup.backendNone"
        });
        StartupHealthDisplay = localization.GetString(state.DiagnosticCode);
        CanTestStartup = state.Backend == StartupRegistrationBackendKind.TaskScheduler &&
                         state.Health == StartupRegistrationHealth.Healthy && !IsStartupSettingsBusy;
        CanRepairStartup = IsStartupRepairRequired(state.Health) && !IsStartupSettingsBusy;
    }
    private static bool IsStartupRepairRequired(StartupRegistrationHealth health) =>
        health is not StartupRegistrationHealth.Healthy and
            not StartupRegistrationHealth.HealthyRegistryFallback and
            not StartupRegistrationHealth.Disabled;
    private void ApplyRegistrationSnapshot(WindowsStartupRegistrationSnapshot snapshot)
    {
        var state = snapshot.EffectiveBackend == StartupRegistrationBackendKind.RegistryRun
            ? snapshot.RegistryRunState : snapshot.TaskSchedulerState;
        state = state with
        {
            ConfiguredByUser = snapshot.ConfiguredBackend != StartupRegistrationBackendKind.None,
            Health = snapshot.OverallHealth,
            DiagnosticCode = snapshot.DiagnosticCode
        };
        ApplyRegistrationHealth(state);
        LaunchAtLogin = state.ConfiguredByUser && state.MatchesCurrentExecutable;
        StartupSettingsMessage = localization.GetString(snapshot.DiagnosticCode);
    }
    private async Task RefreshStartupJournalAsync(CancellationToken token=default)
    {
        var last=(await startupHealthJournal.ReadAsync(token)).Where(x=>x.Source is StartupLaunchSource.TaskScheduler or StartupLaunchSource.RegistryRun).OrderByDescending(x=>x.ProcessStartedAt).FirstOrDefault();
        StartupLastAttemptDisplay=last?.ProcessStartedAt.ToLocalTime().ToString("g")??"—";
        StartupVisibleDurationDisplay=Duration(last,StartupPhase.PresentationReady);
        StartupReadyDurationDisplay=Duration(last,StartupPhase.Ready);
        StartupFailureDisplay=last?.FailurePhase is { } phase?$"{phase}: {last.FailureCode}":"—";
    }
    private static string Duration(StartupHealthRecord? record,StartupPhase phase)=>record is not null&&record.ElapsedMilliseconds.TryGetValue(phase.ToString(),out var value)?$"{value} ms":"—";

    public void ApplyStartupPreferences(StartupPreferenceSnapshot snapshot)
    {
        _suppressPresentationSave = true;
        SelectedStartupPresentationMode = snapshot.PresentationMode;
        _persistedStartupPresentationMode = snapshot.PresentationMode;
        _suppressPresentationSave = false;
        _suppressCloseBehaviorSave = true;
        SelectedMainWindowCloseBehavior = snapshot.CloseBehavior;
        _persistedCloseBehavior = snapshot.CloseBehavior;
        _suppressCloseBehaviorSave = false;
        LaunchAtLogin = snapshot.LaunchAtLogin;
    }

    partial void OnSelectedMainWindowCloseBehaviorChanged(MainWindowCloseBehavior value)
    {
        if (!_initialized || _suppressCloseBehaviorSave) return;
        var version = Interlocked.Increment(ref _closeBehaviorSaveVersion);
        _ = SaveCloseBehaviorObservedAsync(value, version);
    }

    private async Task SaveCloseBehaviorObservedAsync(MainWindowCloseBehavior value, int version)
    {
        await _closeBehaviorSaveGate.WaitAsync();
        try
        {
            if (version != Volatile.Read(ref _closeBehaviorSaveVersion)) return;
            IsCloseBehaviorBusy = true;
            await PersistCloseBehaviorAsync(value, CancellationToken.None);
            if (version == Volatile.Read(ref _closeBehaviorSaveVersion))
                CloseBehaviorMessage = localization.GetString("closeBehavior.saved");
        }
        catch
        {
            if (version == Volatile.Read(ref _closeBehaviorSaveVersion))
            {
                _suppressCloseBehaviorSave = true;
                SelectedMainWindowCloseBehavior = _persistedCloseBehavior;
                _suppressCloseBehaviorSave = false;
                CloseBehaviorMessage = localization.GetString("closeBehavior.saveFailed");
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _closeBehaviorSaveVersion))
                IsCloseBehaviorBusy = false;
            _closeBehaviorSaveGate.Release();
        }
    }

    public async Task SetMainWindowCloseBehaviorAsync(
        MainWindowCloseBehavior value,
        CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref _closeBehaviorSaveVersion);
        await _closeBehaviorSaveGate.WaitAsync(cancellationToken);
        try
        {
            if (_persistedCloseBehavior == value) return;
            await PersistCloseBehaviorAsync(value, cancellationToken);
            _suppressCloseBehaviorSave = true;
            try { SelectedMainWindowCloseBehavior = value; }
            finally { _suppressCloseBehaviorSave = false; }
            if (version == Volatile.Read(ref _closeBehaviorSaveVersion))
                CloseBehaviorMessage = localization.GetString("closeBehavior.saved");
            sessionLog.Information("Settings", $"Main window close behavior changed by Web Settings: {value}.");
        }
        finally
        {
            _closeBehaviorSaveGate.Release();
        }
    }

    private async Task PersistCloseBehaviorAsync(MainWindowCloseBehavior value, CancellationToken cancellationToken)
    {
        if (_persistedCloseBehavior == value) return;
        await settingsService.UpdateAsync(settings => settings.MainWindowCloseBehavior = value, cancellationToken);
        _persistedCloseBehavior = value;
    }

    internal void SetCloseBehaviorFromCloseDialog(MainWindowCloseBehavior value)
    {
        _persistedCloseBehavior = value;
        _suppressCloseBehaviorSave = true;
        SelectedMainWindowCloseBehavior = value;
        _suppressCloseBehaviorSave = false;
    }

    public async Task RestoreAuthorizedStartupModulesAsync(CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.ReadAsync(cancellationToken);
        var succeeded = 0; var skipped = 0; var failed = 0;
        foreach (var authorization in settings.StartupModules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var module = Modules.FirstOrDefault(item => item.Id == authorization.ModuleId);
            if (module is null)
            {
                skipped++;
                continue;
            }
            module.IsStartupAuthorizationBusy = true;
            try
            {
                await using var executionLease = await executionGate.EnterExecutionAsync(
                    module.Id, cancellationToken);
                // One final payload read immediately before execution minimizes the
                // filesystem TOCTOU window without duplicating restore-time hashing.
                bool matches;
                try
                {
                    var moduleSnapshot = module.Module;
                    var authorizationSnapshot = authorization;
                    matches = await Task.Run(() => fingerprintService.MatchesAsync(
                        moduleSnapshot,
                        authorizationSnapshot,
                        cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Startup module fingerprint unavailable: {exception.GetType().Name}");
                    module.StartupAuthorizationState = StartupAuthorizationState.Unavailable;
                    module.StartupAuthorizationMessage = localization.GetString("startup.moduleUnavailable");
                    skipped++;
                    continue;
                }

                if (!matches)
                {
                    module.IsStartupEnabled = false;
                    module.StartupAuthorizationState = StartupAuthorizationState.ChangedNeedsConfirmation;
                    module.StartupAuthorizationMessage = localization.GetString("startup.moduleChanged");
                    skipped++;
                    continue;
                }
                if (IsOutOfProcessWpf(module.Id))
                {
                    if (!await updateRuntimeCoordinator.RestorePreviousRuntimeStateAsync(module.Id,
                            new(false, authorization.ActivateOnStartup, true, true), cancellationToken))
                        throw new ModuleRuntimeException("The startup module worker could not be restored.");
                    module.UpdateOutOfProcessRuntimeState(moduleProcessBroker.GetState(module.Id));
                }
                else
                {
                    await runtimeManager.LoadAsync(module.Id, applicationPaths.ModuleDataDirectory, cancellationToken);
                    if (authorization.ActivateOnStartup) await runtimeManager.ActivateAsync(module.Id, cancellationToken);
                    RefreshRuntimeProjection(module);
                }
                succeeded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (ModuleExecutionBlockedException)
            {
                module.UpdateExecutionReadiness(executionGate.GetReadiness(module.Id));
                skipped++;
            }
            catch (Exception exception)
            {
                RefreshRuntimeProjection(module);
                module.RuntimeError = exception.Message;
                failed++;
            }
            finally { module.IsStartupAuthorizationBusy = false; }
        }
        UpdateStatistics();
        if (settings.StartupModules.Count > 0)
            StatusMessage = localization.GetString("startup.restoreSummary", succeeded, skipped, failed);
    }

    public void RefreshModuleExecutionReadiness()
    {
        foreach (var module in Modules)
        {
            module.UpdateExecutionReadiness(executionGate.GetReadiness(module.Id));
        }
    }

    [RelayCommand]
    private async Task ToggleModuleStartupAsync(DiscoveredModuleViewModel module)
    {
        if (!module.CanChangeStartupAuthorization) return;
        var desired = module.IsStartupEnabled;
        var previousEnabled = !desired;
        var previousState = module.StartupAuthorizationState;
        var previousMessage = module.StartupAuthorizationMessage;
        module.IsStartupAuthorizationBusy = true;
        try
        {
            await PersistModuleStartupAuthorizationAsync(module, desired, CancellationToken.None);
            ApplyStartupAuthorizationProjection(module, desired);
            if (desired)
            {
                if (previousState == StartupAuthorizationState.NotEnabled) StartupAuthorizationCount++;
            }
            else
            {
                StartupAuthorizationCount = Math.Max(0, StartupAuthorizationCount - 1);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Startup module authorization update failed: {exception.GetType().Name}");
            module.IsStartupEnabled = previousEnabled;
            module.StartupAuthorizationState = previousState;
            module.StartupAuthorizationMessage = previousMessage;
            StartupSettingsMessage = localization.GetString("startup.moduleAuthorizationFailed");
        }
        finally { module.IsStartupAuthorizationBusy = false; }
    }

    public async Task<WebModuleLifecycleResult> SetModuleStartupAuthorizationFromWebAsync(
        string moduleId, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var module = Modules.FirstOrDefault(item => string.Equals(item.Id, moduleId, StringComparison.Ordinal));
        if (module is null) return WebModuleLifecycleResult.NotFound;
        if (module.IsStartupAuthorizationBusy || module.IsBusy) return WebModuleLifecycleResult.Busy;
        if (module.IsExecutionBlocked) return WebModuleLifecycleResult.ExecutionBlocked;
        if (!module.IsValid || module.StartupAuthorizationState == StartupAuthorizationState.Unavailable ||
            !module.CanChangeStartupAuthorization) return WebModuleLifecycleResult.Unavailable;
        if (enabled && module.IsStartupEnabled && module.StartupAuthorizationState == StartupAuthorizationState.Enabled ||
            !enabled && module.StartupAuthorizationState == StartupAuthorizationState.NotEnabled)
            return WebModuleLifecycleResult.Succeeded;

        var previousEnabled = module.IsStartupEnabled;
        var previousState = module.StartupAuthorizationState;
        var previousMessage = module.StartupAuthorizationMessage;
        var previousCount = StartupAuthorizationCount;
        module.IsStartupAuthorizationBusy = true;
        try
        {
            await PersistModuleStartupAuthorizationAsync(module, enabled, cancellationToken);
            ApplyStartupAuthorizationProjection(module, enabled);
            if (enabled)
            {
                if (previousState == StartupAuthorizationState.NotEnabled) StartupAuthorizationCount++;
            }
            else if (previousState is StartupAuthorizationState.Enabled or StartupAuthorizationState.ChangedNeedsConfirmation)
                StartupAuthorizationCount = Math.Max(0, StartupAuthorizationCount - 1);
            return WebModuleLifecycleResult.Succeeded;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Debug.WriteLine($"Startup module authorization update failed: {exception.GetType().Name}");
            module.IsStartupEnabled = previousEnabled;
            module.StartupAuthorizationState = previousState;
            module.StartupAuthorizationMessage = previousMessage;
            StartupAuthorizationCount = previousCount;
            StartupSettingsMessage = localization.GetString("startup.moduleAuthorizationFailed");
            return WebModuleLifecycleResult.Failed;
        }
        finally { module.IsStartupAuthorizationBusy = false; }
    }

    private async Task PersistModuleStartupAuthorizationAsync(
        DiscoveredModuleViewModel module, bool enabled, CancellationToken cancellationToken)
    {
        if (enabled)
        {
            var authorization = await fingerprintService.CreateAuthorizationAsync(module.Module, cancellationToken);
            await settingsService.UpdateAsync(settings =>
            {
                settings.StartupModules.RemoveAll(item => item.ModuleId == module.Id);
                settings.StartupModules.Add(authorization);
            }, cancellationToken);
            return;
        }

        await settingsService.UpdateAsync(settings =>
            settings.StartupModules.RemoveAll(item => item.ModuleId == module.Id), cancellationToken);
    }

    private void ApplyStartupAuthorizationProjection(DiscoveredModuleViewModel module, bool enabled)
    {
        module.IsStartupEnabled = enabled;
        module.StartupAuthorizationState = enabled ? StartupAuthorizationState.Enabled : StartupAuthorizationState.NotEnabled;
        module.StartupAuthorizationMessage = localization.GetString(enabled ? "startup.moduleEnabled" : "startup.moduleDisabled");
    }

    [RelayCommand]
    private async Task ApplyLaunchAtLoginAsync()
    {
        if (IsStartupSettingsBusy || !executionEnvironment.AllowWindowsStartupRegistration) return;
        IsStartupSettingsBusy = true;
        var desired = LaunchAtLogin;
        try
        {
            await startupRegistrationService.SetEnabledAsync(desired);
            ApplyRegistrationSnapshot(await startupRegistrationService.GetSnapshotAsync());
            StartupSettingsMessage = localization.GetString(LaunchAtLogin ? "startup.registrationEnabled" : "startup.registrationDisabled");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Startup registration update failed: {exception.GetType().Name}");
            try { ApplyRegistrationSnapshot(await startupRegistrationService.GetSnapshotAsync()); }
            catch (Exception snapshotException)
            {
                Debug.WriteLine($"Startup registration refresh failed: {snapshotException.GetType().Name}");
            }
            if (_lastStartupRegistrationState?.Health != StartupRegistrationHealth.PartialFailure)
                StartupSettingsMessage = localization.GetString("startup.registrationFailed");
        }
        finally { IsStartupSettingsBusy = false; }
    }

    partial void OnIsStartupSettingsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfigureWindowsStartup));
        if (_lastStartupRegistrationState is not null) ApplyRegistrationHealth(_lastStartupRegistrationState);
    }

    partial void OnSelectedStartupPresentationModeChanged(StartupPresentationMode value)
    {
        if (!_initialized || _suppressPresentationSave) return;
        var version = Interlocked.Increment(ref _presentationSaveVersion);
        _ = SaveStartupPresentationAsync(value, version);
    }

    private async Task SaveStartupPresentationAsync(
        StartupPresentationMode newValue, int version)
    {
        IsStartupSettingsBusy = true;
        try
        {
            await _presentationSaveGate.WaitAsync();
            try
            {
                if (version != Volatile.Read(ref _presentationSaveVersion)) return;
                await PersistStartupPresentationModeAsync(newValue, CancellationToken.None);
                StartupSettingsMessage = localization.GetString("startup.presentationSaved");
            }
            finally { _presentationSaveGate.Release(); }
        }
        catch (Exception)
        {
            if (version == Volatile.Read(ref _presentationSaveVersion))
            {
                _suppressPresentationSave = true;
                SelectedStartupPresentationMode = _persistedStartupPresentationMode;
                _suppressPresentationSave = false;
                StartupSettingsMessage = localization.GetString("startup.presentationSaveFailed");
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _presentationSaveVersion)) IsStartupSettingsBusy = false;
        }
    }

    public async Task<WebSettingsMutationResult> SetLaunchAtLoginFromWebAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!CanConfigureWindowsStartup) return WebSettingsMutationResult.Unavailable;
        if (LaunchAtLogin == enabled) return WebSettingsMutationResult.Succeeded;
        IsStartupSettingsBusy = true;
        try
        {
            await startupRegistrationService.SetEnabledAsync(enabled, cancellationToken);
            var snapshot = await startupRegistrationService.GetSnapshotAsync(cancellationToken);
            ApplyRegistrationSnapshot(snapshot);
            if (LaunchAtLogin != enabled) return WebSettingsMutationResult.Failed;
            StartupSettingsMessage = localization.GetString(enabled ? "startup.registrationEnabled" : "startup.registrationDisabled");
            return WebSettingsMutationResult.Succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            try { ApplyRegistrationSnapshot(await startupRegistrationService.GetSnapshotAsync(cancellationToken)); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
            StartupSettingsMessage = localization.GetString("startup.registrationFailed");
            return WebSettingsMutationResult.Failed;
        }
        finally { IsStartupSettingsBusy = false; }
    }

    public async Task SetStartupPresentationModeAsync(
        StartupPresentationMode value,
        CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref _presentationSaveVersion);
        await _presentationSaveGate.WaitAsync(cancellationToken);
        try
        {
            if (_persistedStartupPresentationMode == value) return;
            await PersistStartupPresentationModeAsync(value, cancellationToken);
            _suppressPresentationSave = true;
            try { SelectedStartupPresentationMode = value; }
            finally { _suppressPresentationSave = false; }
            if (version == Volatile.Read(ref _presentationSaveVersion))
                StartupSettingsMessage = localization.GetString("startup.presentationSaved");
            sessionLog.Information("Settings", $"Startup presentation preference changed by Web Settings: {value}.");
        }
        finally
        {
            _presentationSaveGate.Release();
        }
    }

    private async Task PersistStartupPresentationModeAsync(
        StartupPresentationMode value,
        CancellationToken cancellationToken)
    {
        if (_persistedStartupPresentationMode == value) return;
        await settingsService.UpdateAsync(settings => settings.StartupPresentationMode = value, cancellationToken);
        _persistedStartupPresentationMode = value;
    }

    [RelayCommand]
    private async Task ClearMissingStartupAuthorizationsAsync()
    {
        if (IsStartupSettingsBusy) return;
        IsStartupSettingsBusy = true;
        var discoveredIds = Modules.Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
        try
        {
            await settingsService.UpdateAsync(settings =>
                settings.StartupModules.RemoveAll(item => !discoveredIds.Contains(item.ModuleId)));
            StartupAuthorizationCount = Math.Max(
                0,
                StartupAuthorizationCount - MissingStartupAuthorizationCount);
            MissingStartupAuthorizationCount = 0;
            StartupSettingsMessage = localization.GetString("startup.missingCleared");
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Missing startup authorization cleanup failed: {exception.GetType().Name}");
            StartupSettingsMessage = localization.GetString("startup.missingClearFailed");
        }
        finally { IsStartupSettingsBusy = false; }
    }

    partial void OnStartupAuthorizationCountChanged(int value) =>
        OnPropertyChanged(nameof(StartupAuthorizationSummary));

    partial void OnMissingStartupAuthorizationCountChanged(int value) =>
        OnPropertyChanged(nameof(MissingStartupAuthorizationSummary));

    [RelayCommand]
    private async Task ImportModuleAsync()
    {
        try
        {
            var result = await ImportModuleFromWebAsync(CancellationToken.None);
            if (result.Disposition == WebModuleImportDisposition.Imported)
            {
                var importedModule = Modules.FirstOrDefault(module => module.Id == result.ImportedModuleId);
                StatusMessage = localization.GetString(
                    "status.moduleImportedNextStep",
                    importedModule?.DisplayName ?? result.ImportedModuleId ?? string.Empty);
            }
        }
        catch (Exception exception)
        {
            StatusMessage = localization.GetString(
                "status.moduleImportFailed",
                exception.Message);
        }
    }

    public async Task<WebModuleImportOperationResult> ImportModuleFromWebAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new OpenFileDialog
        {
            Title = localization.GetString("modules.importDialogTitle"),
            Filter = localization.GetString("modules.importFileFilter"),
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return new(WebModuleImportDisposition.Cancelled, null);
        }

        var moduleId = await modulePackageImporter.ImportAsync(
            dialog.FileName,
            applicationPaths.UserModulesDirectory,
            Modules.Select(module => module.Id).ToArray(),
            cancellationToken);
        await RefreshModulesCoreAsync(cancellationToken);
        SelectedModule = Modules.FirstOrDefault(module => module.Id == moduleId);
        SelectedNavigationKey = "Modules";
        return new(WebModuleImportDisposition.Imported, moduleId);
    }
}
