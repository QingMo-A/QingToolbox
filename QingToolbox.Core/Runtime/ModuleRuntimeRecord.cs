using QingToolbox.Abstractions.Modules;
using QingToolbox.ModuleLoader;

namespace QingToolbox.Core.Runtime;

public sealed class ModuleRuntimeRecord
{
    internal ModuleRuntimeRecord(DiscoveredModule discoveredModule)
    {
        DiscoveredModule = discoveredModule;
        State = discoveredModule.State;
    }

    public DiscoveredModule DiscoveredModule { get; }

    public ModuleManifest Manifest => DiscoveredModule.Manifest;

    public ModuleState State { get; internal set; }

    public LoadedModuleHandle? Handle { get; internal set; }

    public string? LastError { get; internal set; }

    public bool IsLoaded => Handle is not null && !Handle.IsDisposed;
}
