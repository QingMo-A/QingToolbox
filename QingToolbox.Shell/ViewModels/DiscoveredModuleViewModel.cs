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
        State = module.State.ToString();
        ErrorCount = module.Errors.Count;
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
    public string State { get; }
    public int ErrorCount { get; }
    public bool IsValid { get; }
    public string ModuleDirectory { get; }
    public string ManifestPath { get; }
    public IReadOnlyList<string> Errors { get; }
}
