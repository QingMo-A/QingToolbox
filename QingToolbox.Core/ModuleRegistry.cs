using QingToolbox.Abstractions.Modules;

namespace QingToolbox.Core;

public sealed class ModuleRegistry
{
    private readonly List<DiscoveredModule> _modules = [];

    public IReadOnlyList<DiscoveredModule> Modules => _modules;

    public void Register(DiscoveredModule module)
    {
        _modules.Add(module);
    }

    public void Clear()
    {
        _modules.Clear();
    }

    public void ReplaceAll(IEnumerable<DiscoveredModule> modules)
    {
        _modules.Clear();
        _modules.AddRange(modules);
    }
}
