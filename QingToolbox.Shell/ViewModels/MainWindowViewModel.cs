using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QingToolbox.Core;
using QingToolbox.ModuleLoader;

namespace QingToolbox.Shell.ViewModels;

public sealed partial class MainWindowViewModel(
    ModuleManifestScanner scanner,
    ModuleRegistry registry) : ObservableObject
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

    public string Title => "Qing Toolbox";

    public string PinLabel => IsSidebarPinned ? "Unpin Sidebar" : "Pin Sidebar";

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
            registry.ReplaceAll(discoveredModules);

            Modules.Clear();
            foreach (var module in discoveredModules)
            {
                Modules.Add(new DiscoveredModuleViewModel(module));
            }

            StatusMessage = $"Found {Modules.Count} module(s).";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Failed to scan modules: {exception.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }
}
