using CommunityToolkit.Mvvm.ComponentModel;
using QingToolbox.Abstractions.Modules;

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
}
