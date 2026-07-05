using QingToolbox.Abstractions.Modules;
using QingToolbox.ModuleLoader;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    try
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
        var discoveredModules = await scanner.ScanAsync(modulesDirectory);
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
            throw new InvalidOperationException($"Hello module manifest is invalid:{Environment.NewLine}{errors}");
        }

        var weakReference = await LoadAndUnloadAsync(helloModule, dataDirectory);

        Console.WriteLine("Verifying collectible AssemblyLoadContext unload...");
        if (!ModuleUnloadVerifier.WaitForUnload(weakReference))
        {
            Console.Error.WriteLine("Module load context was not unloaded.");
            return 1;
        }

        Console.WriteLine("Module load context unloaded successfully.");
        Console.WriteLine("Smoke test passed.");
        return 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine("Smoke test failed.");
        Console.Error.WriteLine(exception);
        return 1;
    }
}

static async Task<WeakReference> LoadAndUnloadAsync(
    DiscoveredModule discoveredModule,
    string dataRootDirectory)
{
    var loader = new InProcessModuleLoader();
    var handle = await loader.LoadAsync(discoveredModule, dataRootDirectory);
    var weakReference = handle.LoadContextWeakReference;

    try
    {
        Console.WriteLine($"Loaded module: {handle.Manifest.Id}");
        Console.WriteLine("Activating module...");
        await handle.Module.OnActivateAsync();
        Console.WriteLine("Deactivating module...");
        await handle.Module.OnDeactivateAsync();
    }
    finally
    {
        Console.WriteLine("Disposing module handle...");
        await handle.DisposeAsync();
    }

    return weakReference;
}

static SmokeTestOptions ParseOptions(string[] args)
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

static string FindRepositoryRoot(string startDirectory)
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

internal sealed record SmokeTestOptions(
    string? ModulesDirectory,
    string? DataDirectory);
