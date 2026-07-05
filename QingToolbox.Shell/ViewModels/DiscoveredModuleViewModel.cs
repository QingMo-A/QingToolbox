using CommunityToolkit.Mvvm.ComponentModel;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Runtime;

namespace QingToolbox.Shell.ViewModels;

public sealed partial class DiscoveredModuleViewModel : ObservableObject
{
    public DiscoveredModuleViewModel(DiscoveredModule module)
    {
        Id = module.Manifest.Id;
        Name = module.Manifest.Name;
        Version = module.Manifest.Version;
        Description = module.Manifest.Description ?? "No description.";
        RuntimeType = module.Manifest.RuntimeType.ToString();
        LoadMode = module.Manifest.LoadMode.ToString();
        PermissionsText = module.Manifest.Permissions.Count == 0
            ? "No permissions"
            : string.Join(", ", module.Manifest.Permissions);
        Entry = module.Manifest.Entry;
        Author = module.Manifest.Author ?? "Unknown";
        MinimumHostVersion = module.Manifest.MinimumHostVersion ?? "Not specified";
        State = module.State.ToString();
        ErrorCount = module.Errors.Count;
        HasErrors = ErrorCount > 0;
        ErrorSummary = HasErrors ? $"{ErrorCount} issue(s)" : "No issues";
        StateBadgeText = State;
        IsValid = module.IsValid;
        ModuleDirectory = module.ModuleDirectory;
        ManifestPath = module.ManifestPath;
        Errors = module.Errors
            .Select(error => $"{error.Code}: {error.Message}")
            .ToArray();
        _runtimeState = State;
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Description { get; }
    public string RuntimeType { get; }
    public string LoadMode { get; }
    public string PermissionsText { get; }
    public string Entry { get; }
    public string Author { get; }
    public string MinimumHostVersion { get; }
    public string State { get; }
    public string StateBadgeText { get; }
    public int ErrorCount { get; }
    public bool HasErrors { get; }
    public string ErrorSummary { get; }
    public bool IsValid { get; }
    public string ModuleDirectory { get; }
    public string ManifestPath { get; }
    public IReadOnlyList<string> Errors { get; }

    [ObservableProperty]
    private string _runtimeState;

    [ObservableProperty]
    private string _runtimeError = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasRuntimeError => !string.IsNullOrWhiteSpace(RuntimeError);

    public bool CanLoad =>
        !IsBusy && RuntimeState is "NotLoaded" or "Unloaded";

    public bool CanActivate =>
        !IsBusy && RuntimeState is "Loaded" or "Deactivated";

    public bool CanDeactivate =>
        !IsBusy && RuntimeState == "Running";

    public bool CanUnload =>
        !IsBusy && RuntimeState is "Loaded" or "Running" or "Deactivated" or "Failed";

    public void UpdateRuntimeState(ModuleRuntimeRecord? record)
    {
        RuntimeState = record?.State.ToString() ?? State;
        RuntimeError = record?.LastError ?? string.Empty;
    }

    partial void OnRuntimeStateChanged(string value)
    {
        NotifyCommandStatesChanged();
    }

    partial void OnRuntimeErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasRuntimeError));
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommandStatesChanged();
    }

    private void NotifyCommandStatesChanged()
    {
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(CanActivate));
        OnPropertyChanged(nameof(CanDeactivate));
        OnPropertyChanged(nameof(CanUnload));
    }
}
