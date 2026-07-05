using QingToolbox.Abstractions;

namespace QingToolbox.Core;

public sealed class ModuleRegistry
{
    private readonly List<ModuleManifest> _modules = [];
    public IReadOnlyList<ModuleManifest> Modules => _modules;
    public void Register(ModuleManifest manifest) => _modules.Add(manifest);
}
