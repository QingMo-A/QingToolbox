using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Runtime;
using QingToolbox.Core.Settings;
using QingToolbox.ModuleLoader;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Windowing;

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
        var configuration = GetBuildConfiguration(AppContext.BaseDirectory);
        var shellOutput = Path.Combine(
            repositoryRoot,
            "QingToolbox.Shell",
            "bin",
            configuration,
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
        VerifyUserSettingsAsync().GetAwaiter().GetResult();
        VerifyWindowChromeContracts();
        RunQmodImportScenario(repositoryRoot);
        Console.WriteLine("Smoke test passed.");
        return 0;
    }

    private static void VerifyWindowChromeContracts()
    {
        Console.WriteLine("Verifying custom window chrome contracts...");
        static IntPtr Pack(short x, short y) =>
            new(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        Require(WindowHitTestService.DecodeScreenPoint(Pack(120, 250)) == new Point(120, 250),
            "Positive screen coordinates were decoded incorrectly.");
        Require(WindowHitTestService.DecodeScreenPoint(Pack(-120, -30)) == new Point(-120, -30),
            "Negative screen coordinates were decoded incorrectly.");
        Require(WindowHitTestService.Contains(new Point(10, 10), 46, 48),
            "An interior caption point was not hit.");
        Require(WindowHitTestService.Contains(new Point(46, 48), 46, 48),
            "A caption boundary point was not hit.");
        Require(!WindowHitTestService.Contains(new Point(47, 10), 46, 48),
            "An exterior caption point was hit.");

        Require(WindowCaptionCapabilities.FromResizeMode(ResizeMode.CanResize) == new WindowCaptionCapabilities(true, true),
            "CanResize capabilities are incorrect.");
        Require(WindowCaptionCapabilities.FromResizeMode(ResizeMode.CanMinimize) == new WindowCaptionCapabilities(true, false),
            "CanMinimize capabilities are incorrect.");
        Require(WindowCaptionCapabilities.FromResizeMode(ResizeMode.NoResize) == new WindowCaptionCapabilities(false, false),
            "NoResize capabilities are incorrect.");
        Require(WindowCaptionCapabilities.UsesRestoreAction(WindowState.Maximized),
            "Maximized windows must expose Restore.");
        Require(!WindowCaptionCapabilities.UsesRestoreAction(WindowState.Normal),
            "Normal windows must expose Maximize.");
        var maximizeInteraction = new MaximizeButtonInteractionState();
        maximizeInteraction.Press();
        Require(maximizeInteraction.Release(true) && !maximizeInteraction.IsPressed,
            "A normal maximize release must invoke exactly once and clear Pressed.");
        maximizeInteraction.Press();
        maximizeInteraction.Cancel();
        Require(!maximizeInteraction.Release(true) && !maximizeInteraction.IsPressed,
            "A cancelled maximize press must not invoke or remain Pressed.");

        var workArea = new Rect(-1280, -900, 1280, 900);
        var badgeSize = new Size(68, 68);
        Require(FloatingBadgePlacement.Initial(workArea, badgeSize) == new Point(-92, -876),
            "Floating badge initial placement is incorrect for negative coordinates.");
        Require(FloatingBadgePlacement.Constrain(new Point(100, 100), workArea, badgeSize) == new Point(-68, -68),
            "Floating badge right/bottom constraints are incorrect.");
        Require(FloatingBadgePlacement.Constrain(new Point(double.NaN, 0), workArea, badgeSize) == workArea.TopLeft,
            "Invalid saved badge positions must fall back safely.");
        var tinyArea = new Rect(-20, -20, 30, 30);
        Require(FloatingBadgePlacement.Constrain(new Point(50, 50), tinyArea, badgeSize) == tinyArea.TopLeft,
            "A badge must remain visible in a smaller work area.");

        var secondMonitor = new MonitorWorkArea(
            @"\\.\DISPLAY2", new Rect(-2560, -200, 1280, 1024),
            new Rect(-2560, -160, 1280, 984), 144, 144);
        var savedPoint = FloatingBadgePlacement.PositionFromRatios(secondMonitor, new Size(102, 102), .25, .75);
        var savedRatios = FloatingBadgePlacement.RatiosFromPosition(secondMonitor, savedPoint, new Size(102, 102));
        Require(Math.Abs(savedRatios.Horizontal - .25) < .0001 && Math.Abs(savedRatios.Vertical - .75) < .0001,
            "Monitor-local badge ratios must round-trip on a negative-coordinate mixed-DPI monitor.");
        var resizedMonitor = secondMonitor with { PixelWorkArea = new Rect(-1920, -120, 960, 720) };
        var resizedPoint = FloatingBadgePlacement.PositionFromRatios(resizedMonitor, new Size(102, 102), savedRatios.Horizontal, savedRatios.Vertical);
        Require(resizedMonitor.PixelWorkArea.Contains(resizedPoint),
            "A saved badge ratio must remain in the changed work area.");
        var constrainedWindow = FloatingBadgePlacement.ConstrainWindowBounds(
            new Rect(-4000, -2000, 2000, 1600), resizedMonitor.PixelWorkArea, new Size(500, 580));
        Require(resizedMonitor.PixelWorkArea.Contains(constrainedWindow),
            "An oversized main window must be constrained to the current work area.");

        var badgeState = new FloatingBadgeStateMachine();
        Require(badgeState.TryBeginEnter() && !badgeState.TryBeginEnter(),
            "Repeated floating badge entry must be rejected.");
        Require(badgeState.TryCompleteEnter(), "Floating badge entry must complete.");
        Require(badgeState.TryBeginRestore() && !badgeState.TryBeginRestore(),
            "Repeated floating badge restore must be rejected.");
        Require(badgeState.TryCompleteRestore(), "Floating badge restore must complete.");
        var exitDuringEnter = new FloatingBadgeStateMachine();
        Require(exitDuringEnter.TryBeginEnter() && exitDuringEnter.TryBeginExit(),
            "Exit must be accepted while entering badge mode.");
        Require(!exitDuringEnter.TryCompleteEnter() && exitDuringEnter.State == FloatingBadgeState.Exiting,
            "Completing entry after exit must not leave Exiting.");
        var exitDuringRestore = new FloatingBadgeStateMachine();
        Require(exitDuringRestore.TryBeginEnter() && exitDuringRestore.TryCompleteEnter() &&
                exitDuringRestore.TryBeginRestore() && exitDuringRestore.TryBeginExit(),
            "Exit must be accepted while restoring.");
        Require(!exitDuringRestore.TryCompleteRestore() && exitDuringRestore.State == FloatingBadgeState.Exiting,
            "Completing restore after exit must not return to Normal.");
        Console.WriteLine("Custom window chrome contracts passed.");
    }

    private static async Task VerifyUserSettingsAsync()
    {
        Console.WriteLine("Verifying durable shared user settings...");
        var root = Path.Combine(Path.GetTempPath(), $"QingToolbox-settings-smoke-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        try
        {
            using var service = new UserSettingsService(path);
            await File.WriteAllTextAsync(path, "{\"Language\":\"zh-CN\",\"UnknownFutureField\":true}");
            var legacyLanguage = await service.ReadAsync();
            Require(legacyLanguage.Language == "zh-CN", "Legacy language-only settings must load.");

            await File.WriteAllTextAsync(path,
                "{\"Language\":\"en-US\",\"FloatingBadgeLeft\":120,\"FloatingBadgeTop\":240,\"HasFloatingBadgePosition\":true}");
            var legacyBadge = await service.ReadAsync();
            Require(legacyBadge.HasFloatingBadgePosition && legacyBadge.FloatingBadgeLeft == 120,
                "Legacy badge coordinates must load.");

            var languageUpdate = service.UpdateAsync(settings => settings.Language = "zh-CN");
            var badgeUpdate = service.UpdateAsync(settings =>
            {
                settings.FloatingBadgeMonitorDeviceName = @"\\.\DISPLAY2";
                settings.FloatingBadgeHorizontalRatio = .2;
                settings.FloatingBadgeVerticalRatio = .8;
            });
            await Task.WhenAll(languageUpdate, badgeUpdate);
            for (var index = 0; index < 12; index++)
                await service.UpdateAsync(settings => settings.SettingsSchemaVersion = index + 1);

            var combined = await service.ReadAsync();
            Require(combined.Language == "zh-CN" && combined.FloatingBadgeMonitorDeviceName == @"\\.\DISPLAY2" &&
                    combined.FloatingBadgeHorizontalRatio == .2 && combined.FloatingBadgeVerticalRatio == .8,
                "Concurrent partial settings updates must not overwrite each other.");
            _ = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Require(!Directory.EnumerateFiles(root, "*.tmp.*").Any(),
                "Atomic settings updates must clean temporary files.");

            await File.WriteAllTextAsync(path, "{ not valid json");
            var recovered = await service.ReadAsync();
            Require(recovered.Language == "system", "Corrupt settings must return safe defaults.");
            Require(Directory.EnumerateFiles(root, "settings.corrupt-*.json").Count() == 1,
                "Corrupt settings must be preserved exactly once.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("Durable shared user settings passed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RunQmodImportScenario(string repositoryRoot)
    {
        Console.WriteLine("Starting .qmod import scenario...");
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"QingToolbox-qmod-smoke-{Guid.NewGuid():N}");
        var packageSource = Path.Combine(testRoot, "package");
        var userModules = Path.Combine(testRoot, "user-modules");
        var validPackage = Path.Combine(testRoot, "hello.qmod");
        var maliciousPackage = Path.Combine(testRoot, "malicious.qmod");

        try
        {
            Directory.CreateDirectory(packageSource);
            File.Copy(
                Path.Combine(repositoryRoot, "QingToolbox.Modules.Hello", "module.json"),
                Path.Combine(packageSource, "module.json"));
            File.Copy(
                Path.Combine(repositoryRoot, "QingToolbox.Modules.Hello", "icon.svg"),
                Path.Combine(packageSource, "icon.svg"));
            File.Copy(
                Path.Combine(
                    repositoryRoot,
                    "QingToolbox.Modules.Hello",
                    "bin",
                    "Release",
                    "net10.0-windows",
                    "QingToolbox.Modules.Hello.dll"),
                Path.Combine(packageSource, "QingToolbox.Modules.Hello.dll"));
            CopyDirectory(
                Path.Combine(repositoryRoot, "QingToolbox.Modules.Hello", "i18n"),
                Path.Combine(packageSource, "i18n"));
            ZipFile.CreateFromDirectory(packageSource, validPackage);

            var importer = new ModulePackageImporter(
                new ModuleManifestReader(),
                new ModuleManifestValidator());
            var importedId = importer.ImportAsync(validPackage, userModules)
                .GetAwaiter()
                .GetResult();
            if (importedId != "qing.hello" ||
                !File.Exists(Path.Combine(userModules, importedId, "module.json")))
            {
                throw new InvalidOperationException("Valid .qmod import did not complete.");
            }

            try
            {
                importer.ImportAsync(validPackage, userModules, ["qing.hello"])
                    .GetAwaiter()
                    .GetResult();
                throw new InvalidOperationException("Duplicate .qmod import was not rejected.");
            }
            catch (IOException)
            {
                Console.WriteLine("Duplicate module id rejected successfully.");
            }

            using (var archive = ZipFile.Open(maliciousPackage, ZipArchiveMode.Create))
            {
                archive.CreateEntry("../escape.txt");
                var manifestEntry = archive.CreateEntry("module.json");
                using var source = File.OpenRead(Path.Combine(packageSource, "module.json"));
                using var destination = manifestEntry.Open();
                source.CopyTo(destination);
            }

            try
            {
                importer.ImportAsync(maliciousPackage, Path.Combine(testRoot, "malicious-target"))
                    .GetAwaiter()
                    .GetResult();
                throw new InvalidOperationException("Path-traversal .qmod was not rejected.");
            }
            catch (InvalidDataException)
            {
                Console.WriteLine("Path-traversal package rejected successfully.");
            }

            Console.WriteLine(".qmod import scenario passed.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)));
        }
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
        CreateRefreshAndReleaseView(runtimeManager);

        runtimeManager.UnloadAsync("qing.hello")
            .GetAwaiter()
            .GetResult();
        EnsureUnloaded(record);

        return weakReference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateRefreshAndReleaseView(
        ModuleRuntimeManager runtimeManager)
    {
        object? view = runtimeManager.CreateView("qing.hello");

        if (view is null)
        {
            throw new InvalidOperationException("CreateView returned null.");
        }

        Console.WriteLine($"Created module view: {view.GetType().FullName}");
        ValidateLocalizedView(view);
        view = null;
        Console.WriteLine("Released module view reference.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ValidateLocalizedView(object view)
    {
        var localizedView =
            view as ILocalizedModuleView ??
            (view as FrameworkElement)?.Tag as ILocalizedModuleView;

        if (localizedView is null)
        {
            throw new InvalidOperationException(
                "Hello view does not expose ILocalizedModuleView.");
        }

        localizedView.RefreshLocalization();
        Console.WriteLine("Hello view localization refresh succeeded.");
    }

    private static ModuleRuntimeManager CreateRuntimeManager(
        DiscoveredModule discoveredModule)
    {
        var runtimeManager = new ModuleRuntimeManager(
            new InProcessModuleLoader(new SmokeTestLocalizationService()));
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

    private sealed class SmokeTestLocalizationService : ILocalizationService
    {
        public CultureInfo CurrentCulture { get; } =
            CultureInfo.GetCultureInfo("en-US");

        public string CurrentLanguageCode => CurrentCulture.Name;

        public event EventHandler? CultureChanged;

        public string GetString(string key) => key;

        public string GetString(string key, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, key, args);

        public string GetModuleString(
            string moduleId,
            string key,
            string? fallback = null)
        {
            if (moduleId == "qing.hello" && key == "view.title")
            {
                return "Smoke Test Hello";
            }

            return fallback ?? key;
        }

        public string GetModuleString(
            string moduleId,
            string key,
            string? fallback,
            params object[] args) =>
            string.Format(
                CultureInfo.InvariantCulture,
                GetModuleString(moduleId, key, fallback),
                args);

        public void RaiseCultureChangedForTest() =>
            CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string GetBuildConfiguration(string baseDirectory)
    {
        var segments = baseDirectory.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.LastOrDefault(segment =>
                   segment is "Debug" or "Release") ??
               "Debug";
    }
}
