using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Runtime;
using QingToolbox.Abstractions.Localization;

namespace QingToolbox.Shell.ViewModels;

public sealed partial class DiscoveredModuleViewModel : ObservableObject
{
    private readonly ILocalizationService _localization;

    public DiscoveredModuleViewModel(
        DiscoveredModule module,
        ILocalizationService localization,
        IReadOnlyList<string>? localizationDiagnostics = null)
    {
        Module = module;
        _localization = localization;
        Id = module.Manifest.Id;
        Name = module.Manifest.Name;
        Version = module.Manifest.Version;
        Description = module.Manifest.Description ??
            localization.GetString("module.noDescription");
        RuntimeType = module.Manifest.RuntimeType.ToString();
        LoadMode = module.Manifest.LoadMode.ToString();
        PermissionsText = module.Manifest.Permissions.Count == 0
            ? localization.GetString("module.noPermissions")
            : string.Join(", ", module.Manifest.Permissions);
        Entry = module.Manifest.Entry;
        Author = module.Manifest.Author ??
            localization.GetString("module.unknownAuthor");
        MinimumHostVersion = module.Manifest.MinimumHostVersion ??
            localization.GetString("module.notSpecified");
        State = module.State.ToString();
        var localizationErrors = localizationDiagnostics?
            .Select(error => $"Localization: {error}") ??
            [];
        Errors = module.Errors
            .Select(error => $"{error.Code}: {error.Message}")
            .Concat(localizationErrors)
            .ToArray();
        ErrorCount = Errors.Count;
        HasErrors = ErrorCount > 0;
        ErrorSummary = HasErrors
            ? $"{ErrorCount} issue(s)"
            : localization.GetString("module.noIssues");
        StateBadgeText = State;
        IsValid = module.IsValid;
        ModuleDirectory = module.ModuleDirectory;
        ManifestPath = module.ManifestPath;
        IconPath = ResolveIconPath(module);
        _runtimeState = State;
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Description { get; }
    public string DisplayName =>
        _localization.GetModuleString(Id, "module.name", Name);
    public string DisplayDescription =>
        _localization.GetModuleString(
            Id,
            "module.description",
            Description);
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
    public string? IconPath { get; }
    public bool HasIcon => !string.IsNullOrWhiteSpace(IconPath);
    public IReadOnlyList<string> Errors { get; }
    internal DiscoveredModule Module { get; }

    [ObservableProperty] private bool _isStartupEnabled;
    [ObservableProperty] private bool _isStartupAuthorizationBusy;
    [ObservableProperty] private string _startupAuthorizationMessage = string.Empty;
    public bool CanChangeStartupAuthorization => IsValid && !IsStartupAuthorizationBusy;

    [ObservableProperty]
    private string _runtimeState;

    [ObservableProperty]
    private string _runtimeError = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasRuntimeError => !string.IsNullOrWhiteSpace(RuntimeError);

    public string DisplayRuntimeState => _localization.GetString(
        RuntimeState switch
        {
            "NotLoaded" => "runtimeState.notLoaded",
            "Loaded" => "runtimeState.loaded",
            "Running" => "runtimeState.running",
            "Deactivated" => "runtimeState.deactivated",
            "Unloading" => "runtimeState.unloading",
            "Unloaded" => "runtimeState.unloaded",
            "Failed" => "runtimeState.failed",
            _ => RuntimeState
        });

    public bool CanLoad =>
        !IsBusy && RuntimeState is "NotLoaded" or "Unloaded";

    public bool CanActivate =>
        !IsBusy && RuntimeState is "Loaded" or "Deactivated";

    public bool CanDeactivate =>
        !IsBusy && RuntimeState == "Running";

    public bool CanUnload =>
        !IsBusy && RuntimeState is "Loaded" or "Running" or "Deactivated" or "Failed";

    public bool CanOpen =>
        !IsBusy && RuntimeState is "Loaded" or "Running" or "Deactivated";

    public void UpdateRuntimeState(ModuleRuntimeRecord? record)
    {
        RuntimeState = record?.State.ToString() ?? State;
        RuntimeError = record?.LastError ?? string.Empty;
    }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(DisplayDescription));
        OnPropertyChanged(nameof(DisplayRuntimeState));
    }

    partial void OnRuntimeStateChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayRuntimeState));
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

    partial void OnIsStartupAuthorizationBusyChanged(bool value) =>
        OnPropertyChanged(nameof(CanChangeStartupAuthorization));

    private void NotifyCommandStatesChanged()
    {
        OnPropertyChanged(nameof(CanLoad));
        OnPropertyChanged(nameof(CanActivate));
        OnPropertyChanged(nameof(CanDeactivate));
        OnPropertyChanged(nameof(CanUnload));
        OnPropertyChanged(nameof(CanOpen));
    }

    private static string? ResolveIconPath(DiscoveredModule module)
    {
        if (string.IsNullOrWhiteSpace(module.Manifest.Icon) ||
            !string.Equals(
                Path.GetExtension(module.Manifest.Icon),
                ".svg",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var moduleDirectory = Path.GetFullPath(module.ModuleDirectory);
        var iconPath = Path.GetFullPath(
            Path.Combine(moduleDirectory, module.Manifest.Icon));
        var modulePrefix = moduleDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return iconPath.StartsWith(
                modulePrefix,
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(iconPath)
                ? iconPath
                : null;
    }
}
