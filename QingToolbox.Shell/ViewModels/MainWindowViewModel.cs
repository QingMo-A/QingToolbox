using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingToolbox.Core;
using QingToolbox.Core.Runtime;
using QingToolbox.ModuleLoader;

namespace QingToolbox.Shell.ViewModels;

public sealed partial class MainWindowViewModel(
    ModuleManifestScanner scanner,
    ModuleRegistry registry,
    ModuleRuntimeManager runtimeManager) : ObservableObject
{
    [ObservableProperty]
    private string _statusMessage = "Ready.";

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private bool _isSidebarExpanded;

    [ObservableProperty]
    private bool _isSidebarPinned;

    [ObservableProperty]
    private string _selectedNavigationKey = "Modules";

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
    private object? _activeModuleView;

    [ObservableProperty]
    private string _activeModuleTitle = "Modules";

    [ObservableProperty]
    private string _activeModuleId = string.Empty;

    public string Title => "Qing Toolbox";

    public string PinLabel => IsSidebarPinned ? "Unpin Sidebar" : "Pin Sidebar";

    public bool HasActiveModuleView => ActiveModuleView is not null;

    public ObservableCollection<DiscoveredModuleViewModel> Modules { get; } = [];

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
            runtimeManager.ReplaceDiscoveredModules(discoveredModules);
            registry.ReplaceAll(discoveredModules);

            Modules.Clear();
            foreach (var module in discoveredModules)
            {
                var moduleViewModel = new DiscoveredModuleViewModel(module);
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
        if (string.Equals(ActiveModuleId, moduleId, StringComparison.Ordinal))
        {
            CloseActiveModuleView(updateStatus: false);
        }

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
            ActiveModuleView = null;
            ActiveModuleId = string.Empty;
            ActiveModuleTitle = "Modules";

            var view = runtimeManager.CreateView(moduleId);
            moduleViewModel.UpdateRuntimeState(runtimeManager.GetRecord(moduleId));

            if (view is null)
            {
                StatusMessage =
                    $"Module '{moduleViewModel.Name}' did not provide a view.";
                return Task.CompletedTask;
            }

            ActiveModuleView = view;
            ActiveModuleTitle = moduleViewModel.Name;
            ActiveModuleId = moduleId;
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

    [RelayCommand]
    private void CloseModuleView()
    {
        CloseActiveModuleView(updateStatus: true);
    }

    public void CloseActiveModuleView()
    {
        CloseActiveModuleView(updateStatus: false);
    }

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
    }

    partial void OnActiveModuleViewChanged(object? value)
    {
        OnPropertyChanged(nameof(HasActiveModuleView));
    }

    private void CloseActiveModuleView(bool updateStatus)
    {
        ActiveModuleView = null;
        ActiveModuleTitle = "Modules";
        ActiveModuleId = string.Empty;

        if (updateStatus)
        {
            StatusMessage = "Module view closed.";
        }
    }
}
