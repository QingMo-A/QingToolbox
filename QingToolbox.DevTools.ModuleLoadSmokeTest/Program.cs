using System.IO;
using System.Runtime.CompilerServices;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Runtime;
using QingToolbox.ModuleLoader;

namespace QingToolbox.DevTools.ModuleLoadSmokeTest;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Smoke test failed.");
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        var options = ParseOptions(args);
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var shellOutput = Path.Combine(
            repositoryRoot,
            "QingToolbox.Shell",
            "bin",
            "Debug",
            "net10.0-windows");

        var modulesDirectory = Path.GetFullPath(
            options.ModulesDirectory ?? Path.Combine(shellOutput, "Modules"));
        var dataDirectory = Path.GetFullPath(
            options.DataDirectory ?? Path.Combine(shellOutput, "UserData", "modules"));

        Console.WriteLine("QingToolbox module load smoke test");
        Console.WriteLine($"Modules directory: {modulesDirectory}");
        Console.WriteLine($"Data directory:    {dataDirectory}");

        var scanner = new ModuleManifestScanner(
            new ModuleManifestReader(),
            new ModuleManifestValidator());

        Console.WriteLine("Scanning module manifests...");
        var discoveredModules = scanner.ScanAsync(modulesDirectory)
            .GetAwaiter()
            .GetResult();
        var helloModule = discoveredModules.SingleOrDefault(
            module => string.Equals(
                module.Manifest.Id,
                "qing.hello",
                StringComparison.Ordinal));

        if (helloModule is null)
        {
            throw new InvalidOperationException(
                "The qing.hello module was not found. Run scripts/deploy-dev-modules.ps1 first.");
        }

        Console.WriteLine($"Discovered: {helloModule.Manifest.Name} v{helloModule.Manifest.Version}");
        Console.WriteLine($"State:      {helloModule.State}");
        Console.WriteLine($"Entry:      {helloModule.Manifest.Entry}");

        if (!helloModule.IsValid)
        {
            var errors = string.Join(
                Environment.NewLine,
                helloModule.Errors.Select(error => $"  {error.Code}: {error.Message}"));
            throw new InvalidOperationException(
                $"Hello module manifest is invalid:{Environment.NewLine}{errors}");
        }

        var lifecycleWeakReference = RunRuntimeLifecycleAsync(helloModule, dataDirectory)
            .GetAwaiter()
            .GetResult();

        Console.WriteLine("Verifying standard lifecycle AssemblyLoadContext unload...");
        if (!ModuleUnloadVerifier.WaitForUnload(lifecycleWeakReference))
        {
            Console.Error.WriteLine(
                "Module load context was not unloaded after the standard lifecycle.");
            return 1;
        }

        Console.WriteLine("Standard lifecycle context unloaded successfully.");

        var viewWeakReference = RunViewLifecycle(helloModule, dataDirectory);

        Console.WriteLine("Verifying CreateView lifecycle AssemblyLoadContext unload...");
        if (!ModuleUnloadVerifier.WaitForUnload(viewWeakReference))
        {
            Console.Error.WriteLine(
                "Module load context was not unloaded after the CreateView scenario.");
            return 1;
        }

        Console.WriteLine("CreateView scenario unloaded successfully.");
        Console.WriteLine("Smoke test passed.");
        return 0;
    }

    private static async Task<WeakReference> RunRuntimeLifecycleAsync(
        DiscoveredModule discoveredModule,
        string dataRootDirectory)
    {
        var runtimeManager = CreateRuntimeManager(discoveredModule);
        var record = GetHelloRecord(runtimeManager);

        EnsureState(record, ModuleState.NotLoaded);

        Console.WriteLine("Loading module through ModuleRuntimeManager...");
        await runtimeManager.LoadAsync("qing.hello", dataRootDirectory);
        EnsureState(record, ModuleState.Loaded);

        var weakReference = GetLoadContextWeakReference(record);

        Console.WriteLine("Activating module through ModuleRuntimeManager...");
        await runtimeManager.ActivateAsync("qing.hello");
        EnsureState(record, ModuleState.Running);

        Console.WriteLine("Deactivating module through ModuleRuntimeManager...");
        await runtimeManager.DeactivateAsync("qing.hello");
        EnsureState(record, ModuleState.Deactivated);

        Console.WriteLine("Unloading module through ModuleRuntimeManager...");
        await runtimeManager.UnloadAsync("qing.hello");
        EnsureUnloaded(record);

        return weakReference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunViewLifecycle(
        DiscoveredModule discoveredModule,
        string dataRootDirectory)
    {
        Console.WriteLine("Starting CreateView lifecycle scenario...");

        var runtimeManager = CreateRuntimeManager(discoveredModule);
        var record = GetHelloRecord(runtimeManager);
        EnsureState(record, ModuleState.NotLoaded);

        runtimeManager.LoadAsync("qing.hello", dataRootDirectory)
            .GetAwaiter()
            .GetResult();
        EnsureState(record, ModuleState.Loaded);

        var weakReference = GetLoadContextWeakReference(record);
        object? view = runtimeManager.CreateView("qing.hello");

        if (view is null)
        {
            throw new InvalidOperationException("CreateView returned null.");
        }

        Console.WriteLine($"Created module view: {view.GetType().FullName}");

        view = null;
        Console.WriteLine("Released module view reference.");

        runtimeManager.UnloadAsync("qing.hello")
            .GetAwaiter()
            .GetResult();
        EnsureUnloaded(record);

        return weakReference;
    }

    private static ModuleRuntimeManager CreateRuntimeManager(
        DiscoveredModule discoveredModule)
    {
        var runtimeManager = new ModuleRuntimeManager(new InProcessModuleLoader());
        runtimeManager.ReplaceDiscoveredModules([discoveredModule]);
        return runtimeManager;
    }

    private static ModuleRuntimeRecord GetHelloRecord(
        ModuleRuntimeManager runtimeManager)
    {
        return runtimeManager.GetRecord("qing.hello")
            ?? throw new InvalidOperationException(
                "The qing.hello runtime record was not created.");
    }

    private static WeakReference GetLoadContextWeakReference(
        ModuleRuntimeRecord record)
    {
        return record.Handle?.LoadContextWeakReference
            ?? throw new InvalidOperationException(
                "Load context weak reference was not available.");
    }

    private static void EnsureUnloaded(ModuleRuntimeRecord record)
    {
        EnsureState(record, ModuleState.Unloaded);

        if (record.Handle is not null)
        {
            throw new InvalidOperationException(
                "Runtime record retained its module handle after unload.");
        }

        Console.WriteLine("Runtime handle cleared successfully.");
    }

    private static void EnsureState(
        ModuleRuntimeRecord record,
        ModuleState expected)
    {
        if (record.State != expected)
        {
            throw new InvalidOperationException(
                $"Expected state {expected}, but got {record.State}.");
        }

        Console.WriteLine($"State OK: {expected}");
    }

    private static SmokeTestOptions ParseOptions(string[] args)
    {
        string? modulesDirectory = null;
        string? dataDirectory = null;

        for (var index = 0; index < args.Length; index++)
        {
            var option = args[index];
            if (option is not ("--modules" or "--data"))
            {
                throw new ArgumentException($"Unknown argument: {option}");
            }

            if (++index >= args.Length)
            {
                throw new ArgumentException($"A path is required after {option}.");
            }

            if (option == "--modules")
            {
                modulesDirectory = args[index];
            }
            else
            {
                dataDirectory = args[index];
            }
        }

        return new SmokeTestOptions(modulesDirectory, dataDirectory);
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QingToolbox.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate QingToolbox.sln from '{startDirectory}'.");
    }

    private sealed record SmokeTestOptions(
        string? ModulesDirectory,
        string? DataDirectory);
}
