using System.Reflection;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.ModuleLoader;

public sealed class InProcessModuleLoader(ILocalizationService localization)
{
    public async Task<LoadedModuleHandle> LoadAsync(
        DiscoveredModule discoveredModule,
        string dataRootDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(discoveredModule);

        if (!discoveredModule.IsValid)
        {
            throw new ModuleLoadException(
                $"Cannot load invalid module manifest: {discoveredModule.Manifest.Id}");
        }

        var manifest = discoveredModule.Manifest;
        if (manifest.RuntimeType != ModuleRuntimeType.InProcess)
        {
            throw new ModuleLoadException(
                $"Module '{manifest.Id}' is not an in-process module.");
        }

        var entryPath = Path.GetFullPath(
            Path.Combine(discoveredModule.ModuleDirectory, manifest.Entry));

        if (!File.Exists(entryPath))
        {
            throw new ModuleLoadException($"Module entry file was not found: {entryPath}");
        }

        var loadContext = new InProcessModuleLoadContext(discoveredModule.ModuleDirectory);

        try
        {
            var assembly = loadContext.LoadFromAssemblyPath(entryPath);
            var moduleType = FindModuleType(assembly, manifest);
            var module = CreateModule(moduleType, manifest.Id);

            var dataDirectory = Path.GetFullPath(
                Path.Combine(dataRootDirectory, manifest.Id));
            Directory.CreateDirectory(dataDirectory);

            var context = new ModuleContext
            {
                ModuleId = manifest.Id,
                ModuleDirectory = discoveredModule.ModuleDirectory,
                DataDirectory = dataDirectory,
                Localization = localization
            };

            await module.OnLoadAsync(context, cancellationToken);

            return new LoadedModuleHandle(
                manifest,
                discoveredModule.ModuleDirectory,
                module,
                loadContext);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    private static Type FindModuleType(Assembly assembly, ModuleManifest manifest)
    {
        var capabilities = ModuleRuntimeCapabilities.Resolve(manifest);
        var contractType = capabilities switch
        {
            { RuntimeIsolation: ModuleRuntimeIsolation.InProcessCollectible, UiKind: ModuleUiKind.None }
                => typeof(IInProcessServiceModule),
            { RuntimeIsolation: ModuleRuntimeIsolation.OutOfProcess, UiKind: ModuleUiKind.Wpf }
                => typeof(IToolModule),
            { RuntimeIsolation: ModuleRuntimeIsolation.LegacyInProcess }
                => typeof(IToolModule),
            _ => throw new ModuleLoadException(
                $"Module '{manifest.Id}' declares an unsupported runtime/UI capability combination.")
        };
        var moduleTypes = assembly.GetTypes()
            .Where(type =>
                !type.IsAbstract &&
                !type.IsInterface &&
                contractType.IsAssignableFrom(type) &&
                type.GetConstructor(Type.EmptyTypes) is not null)
            .ToArray();

        return moduleTypes.Length switch
        {
            1 => moduleTypes[0],
            0 => throw new ModuleLoadException(
                $"No {contractType.Name} implementation was found in module '{manifest.Id}'."),
            _ => throw new ModuleLoadException(
                $"Multiple {contractType.Name} implementations were found in module '{manifest.Id}'.")
        };
    }

    private static IModuleLifecycle CreateModule(Type moduleType, string moduleId)
    {
        try
        {
            return (IModuleLifecycle)Activator.CreateInstance(moduleType)!;
        }
        catch (Exception exception)
        {
            throw new ModuleLoadException(
                $"Failed to create module instance for '{moduleId}'.",
                exception);
        }
    }
}
