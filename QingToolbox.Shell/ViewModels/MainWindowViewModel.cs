using System.Collections.ObjectModel;
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

namespace QingToolbox.Shell.ViewModels;

public sealed partial class MainWindowViewModel(
    ModuleManifestScanner scanner,
    ModuleRegistry registry,
    ModuleRuntimeManager runtimeManager,
    ModuleWindowManager moduleWindowManager,
    LocalizationManager localizationManager,
    ILocalizationService localization) : ObservableObject
{
    [ObservableProperty]
    private string _statusMessage = localization.GetString("status.ready");

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isSidebarExpanded;

    [ObservableProperty]
    private bool _isSidebarPinned;

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

    public string Title => "Qing Toolbox";
    public LocalizedText Strings { get; } = new(localization);
    public IReadOnlyList<LanguageOption> LanguageOptions =>
        localizationManager.SupportedLanguages;

    public string PinLabel => IsSidebarPinned ? "Unpin Sidebar" : "Pin Sidebar";

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

    partial void OnSelectedNavigationKeyChanged(string value)
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsModulesSelected));
        OnPropertyChanged(nameof(IsRunningSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
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

        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
        var option = LanguageOptions.FirstOrDefault(
            item => item.Code == languageCode);
        StatusMessage = localization.GetString(
            "status.languageChanged",
            option?.NativeName ?? languageCode);
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
            StatusMessage = "Scanning modules...";

            var modulesRoot = Path.Combine(AppContext.BaseDirectory, "Modules");
            var discoveredModules = await scanner.ScanAsync(modulesRoot);
            localizationManager.ClearModuleLocalizations();
            foreach (var module in discoveredModules)
            {
                localizationManager.RegisterModuleLocalization(
                    module.Manifest.Id,
                    module.ModuleDirectory,
                    module.Manifest.Localization,
                    module.Manifest.DefaultLanguage);
            }
            runtimeManager.ReplaceDiscoveredModules(discoveredModules);
            registry.ReplaceAll(discoveredModules);

            Modules.Clear();
            foreach (var module in discoveredModules)
            {
                var moduleViewModel = new DiscoveredModuleViewModel(
                    module,
                    localization);
                moduleViewModel.UpdateRuntimeState(
                    runtimeManager.GetRecord(module.Manifest.Id));
                Modules.Add(moduleViewModel);
            }

            UpdateStatistics();
            StatusMessage = $"Found {Modules.Count} module(s).";
        }
        catch (Exception exception)
        {
            TotalModuleCount = 0;
            ValidModuleCount = 0;
            FailedModuleCount = 0;
            NotLoadedModuleCount = 0;
            LoadedModuleCount = 0;
            RunningModuleCount = 0;
            UnloadedModuleCount = 0;
            StatusMessage = $"Failed to scan modules: {exception.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private Task LoadModuleAsync(string moduleId)
    {
        var dataRootDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "UserData",
            "modules");

        return ExecuteLifecycleAsync(
            moduleId,
            "load",
            () => runtimeManager.LoadAsync(moduleId, dataRootDirectory));
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
            StatusMessage =
                $"Load module '{moduleViewModel.Name}' before opening its view.";
            return Task.CompletedTask;
        }

        moduleViewModel.IsBusy = true;
        moduleViewModel.RuntimeError = string.Empty;

        try
        {
            if (moduleWindowManager.IsWindowOpen(moduleId))
            {
                moduleWindowManager.ActivateWindow(moduleId);
                StatusMessage = $"Focused module '{moduleViewModel.Name}'.";
                return Task.CompletedTask;
            }

            var view = runtimeManager.CreateView(moduleId);
            moduleViewModel.UpdateRuntimeState(runtimeManager.GetRecord(moduleId));

            if (view is null)
            {
                StatusMessage =
                    $"Module '{moduleViewModel.Name}' did not provide a view.";
                return Task.CompletedTask;
            }

            moduleWindowManager.OpenWindow(
                moduleId,
                moduleViewModel.Name,
                view,
                Application.Current.MainWindow);
            StatusMessage = $"Opened module '{moduleViewModel.Name}'.";
        }
        catch (Exception exception)
        {
            moduleViewModel.UpdateRuntimeState(runtimeManager.GetRecord(moduleId));
            if (!moduleViewModel.HasRuntimeError)
            {
                moduleViewModel.RuntimeError = exception.Message;
            }

            StatusMessage =
                $"Failed to open module '{moduleViewModel.Name}': {exception.Message}";
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
        StatusMessage = $"{operation} module '{moduleViewModel.Name}'...";

        try
        {
            await action();
            moduleViewModel.UpdateRuntimeState(runtimeManager.GetRecord(moduleId));
            StatusMessage =
                $"Module '{moduleViewModel.Name}' {operation} completed.";
        }
        catch (Exception exception)
        {
            moduleViewModel.UpdateRuntimeState(runtimeManager.GetRecord(moduleId));
            if (!moduleViewModel.HasRuntimeError)
            {
                moduleViewModel.RuntimeError = exception.Message;
            }

            StatusMessage =
                $"Failed to {operation} module '{moduleViewModel.Name}': {exception.Message}";
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
            module => module.RuntimeState == "Loaded");
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
    }
}
