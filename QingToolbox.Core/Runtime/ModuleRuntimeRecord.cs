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

    /// <summary>
    /// The collectible context from the most recently completed unload. This
    /// remains a weak reference so retaining runtime diagnostics cannot keep a
    /// module alive.
    /// </summary>
    public WeakReference? LastUnloadedLoadContext { get; internal set; }

    public long LoadContextGeneration { get; internal set; }

    public string? LastError { get; internal set; }

    public bool IsLoaded => Handle is not null && !Handle.IsDisposed;
}

public sealed record ModuleRuntimeSnapshot(
    string ModuleId,
    string Version,
    string ModuleDirectory,
    ModuleState State,
    bool HasRuntimeRegistration,
    bool IsActive,
    long LoadContextGeneration,
    string? RuntimeAssemblyInformationalVersion,
    WeakReference? CurrentLoadContext,
    WeakReference? LastUnloadedLoadContext);
