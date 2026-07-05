using System.Reflection;
using System.Runtime.Loader;

namespace QingToolbox.ModuleLoader;

public sealed class InProcessModuleLoadContext : AssemblyLoadContext
{
    private readonly string _moduleDirectory;

    public InProcessModuleLoadContext(string moduleDirectory)
        : base(isCollectible: true)
    {
        _moduleDirectory = Path.GetFullPath(moduleDirectory);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var dependencyPath = Path.Combine(_moduleDirectory, $"{assemblyName.Name}.dll");

        return File.Exists(dependencyPath)
            ? LoadFromAssemblyPath(dependencyPath)
            : null;
    }
}
