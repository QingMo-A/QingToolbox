using System.IO;
using System.IO.Compression;
using System.IO.Pipes;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Runtime;
using QingToolbox.Core.Settings;
using QingToolbox.ModuleLoader;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Windowing;
using QingToolbox.Shell.Startup;

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
        VerifyCloseBehaviorSettings();
        VerifyExecutionEnvironmentContractsAsync().GetAwaiter().GetResult();
        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        VerifyNotificationAreaLifecycleContracts(repositoryRoot);
        VerifyExitFailureIsolation();
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
        VerifyStartupInfrastructureAsync(helloModule).GetAwaiter().GetResult();
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

    private static async Task VerifyExecutionEnvironmentContractsAsync()
    {
        Console.WriteLine("Verifying execution environment isolation...");
        var production = ApplicationLaunchOptions.Parse([]);
        Require(production.Environment.IsProduction && production.Environment.ProfileName == "Default",
            "No-argument Release semantics must remain Production/Default.");
        Require(ApplicationLaunchOptions.Parse(["--startup"]).IsStartupLaunch,
            "Production startup launch was rejected.");
        Require(ApplicationLaunchOptions.Parse(["--environment", "production"]).Environment.IsProduction,
            "Case-insensitive Production name was rejected.");
        void Reject(params string[] args)
        {
            try { _ = ApplicationLaunchOptions.Parse(args); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException($"Invalid launch arguments were accepted: {string.Join(' ', args)}");
        }
        Reject("--environment", "Production", "--data-root", @"C:\sandbox");
        Reject("--environment", "Development", "--data-root", @"C:\sandbox");
        Reject("--environment", "Development", "--profile", "Shell");
        Reject("--environment", "ModuleTest", "--profile", "PowerGuard");
        Reject("--environment", "Development", "--profile", "..");
        Reject("--environment", "Development", "--profile", "Shell", "--data-root", "relative");
        Reject("--startup", "--environment", "Development", "--profile", "Shell", "--data-root", @"C:\sandbox");
        Reject("--unknown");
        Reject("--environment", "Development", "--environment", "ModuleTest", "--profile", "Shell", "--data-root", @"C:\sandbox");
        foreach (var invalidEnvironment in new[] { "0", "1", "2", "3", "999", "-1", "Unknown", " Development " })
            Reject("--environment", invalidEnvironment);
        try
        {
            _ = ApplicationExecutionEnvironment.Sandbox(
                (ApplicationEnvironmentKind)999, "Shell", @"C:\repo\.qingtoolbox\development\Shell");
            throw new InvalidOperationException("Undefined environment kind was accepted by Sandbox().");
        }
        catch (ArgumentException) { }
        try
        {
            _ = ApplicationExecutionEnvironment.Sandbox(
                ApplicationEnvironmentKind.Production, "Default", @"C:\repo\.qingtoolbox\development\Default");
            throw new InvalidOperationException("Production was accepted by Sandbox().");
        }
        catch (ArgumentException) { }

        var root = Path.Combine(Path.GetTempPath(), $"QingToolbox-environment-smoke-{Guid.NewGuid():N}");
        try
        {
            var devRoot = Path.Combine(root, ".qingtoolbox", "development", "Shell");
            var otherProfileRoot = Path.Combine(root, ".qingtoolbox", "development", "Other");
            var testRoot = Path.Combine(root, ".qingtoolbox", "module-test", "Shell");
            var otherRepositoryRoot = Path.Combine(root, "other-repository", ".qingtoolbox", "development", "Shell");
            var dev = ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.Development, "Shell", devRoot);
            var sameDev = ApplicationExecutionEnvironment.Sandbox(
                ApplicationEnvironmentKind.Development, "shell", devRoot.ToUpperInvariant() + Path.DirectorySeparatorChar);
            var otherDev = ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.Development, "Other", otherProfileRoot);
            var otherRootDev = ApplicationExecutionEnvironment.Sandbox(
                ApplicationEnvironmentKind.Development, "Shell", otherRepositoryRoot);
            var moduleTest = ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.ModuleTest, "Shell", testRoot);
            Require(ApplicationLaunchOptions.Parse(["--environment", "development", "--profile", "Shell", "--data-root", devRoot]).Environment.IsDevelopment &&
                    ApplicationLaunchOptions.Parse(["--environment", "moduletest", "--profile", "Shell", "--data-root", testRoot]).Environment.IsModuleTest,
                "Named sandbox environments were not parsed case-insensitively.");
            void RejectSandbox(ApplicationEnvironmentKind kind, string profile, string path)
            {
                try { _ = ApplicationExecutionEnvironment.Sandbox(kind, profile, path); }
                catch (ArgumentException) { return; }
                throw new InvalidOperationException($"Unsafe sandbox layout was accepted: {path}");
            }
            RejectSandbox(ApplicationEnvironmentKind.Development, "Shell", Path.Combine(root, "sandbox"));
            RejectSandbox(ApplicationEnvironmentKind.Development, "Shell", testRoot);
            RejectSandbox(ApplicationEnvironmentKind.Development, "Other", devRoot);
            RejectSandbox(ApplicationEnvironmentKind.Development, "Shell", Path.Combine(root, "development", "Shell"));
            RejectSandbox(ApplicationEnvironmentKind.Development, "Shell", Path.Combine(devRoot, "extra"));
            Require(dev.InstanceScope == sameDev.InstanceScope && dev.InstanceScope != otherDev.InstanceScope &&
                    dev.InstanceScope != otherRootDev.InstanceScope && dev.InstanceScope != moduleTest.InstanceScope,
                "Sandbox instance scopes are not stable and isolated.");
            Require(dev.DisplayName == "QingToolbox [DEV: Shell]" &&
                    moduleTest.DisplayName == "QingToolbox [MODULE TEST: Shell]",
                "Sandbox display names are incorrect.");
            var uniquePrefix = $"EnvironmentSmoke.{Guid.NewGuid():N}.";
            await using var devPrimary = SingleInstanceCoordinator.CreateForScope(uniquePrefix + dev.InstanceScope);
            await using var devSecondary = SingleInstanceCoordinator.CreateForScope(uniquePrefix + dev.InstanceScope);
            await using var otherProfilePrimary = SingleInstanceCoordinator.CreateForScope(uniquePrefix + otherDev.InstanceScope);
            await using var otherRootPrimary = SingleInstanceCoordinator.CreateForScope(uniquePrefix + otherRootDev.InstanceScope);
            await using var moduleTestPrimary = SingleInstanceCoordinator.CreateForScope(uniquePrefix + moduleTest.InstanceScope);
            Require(devPrimary.IsPrimary && !devSecondary.IsPrimary && otherProfilePrimary.IsPrimary &&
                    otherRootPrimary.IsPrimary && moduleTestPrimary.IsPrimary,
                "Sandbox instance scopes did not isolate profiles and environment kinds.");

            var productionPaths = new ApplicationPaths(production.Environment);
            Require(productionPaths.SettingsPath == Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QingToolbox", "settings.json") &&
                    productionPaths.UserModulesDirectory == Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QingToolbox", "Modules") &&
                    productionPaths.ModuleDataDirectory == Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QingToolbox", "Data"),
                "Production paths changed incompatibly.");

            var devPaths = new ApplicationPaths(dev);
            var testPaths = new ApplicationPaths(moduleTest);
            Require(new[] { devPaths.SettingsPath, devPaths.UserModulesDirectory, devPaths.ModuleDataDirectory,
                    devPaths.LogsDirectory, devPaths.CacheDirectory, devPaths.TempDirectory }
                    .All(path => Path.GetFullPath(path).StartsWith(Path.GetFullPath(devRoot) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)),
                "Development writable paths escaped the sandbox.");
            Require(new[] { testPaths.SettingsPath, testPaths.UserModulesDirectory, testPaths.ModuleDataDirectory,
                    testPaths.LogsDirectory, testPaths.CacheDirectory, testPaths.TempDirectory }
                    .All(path => Path.GetFullPath(path).StartsWith(Path.GetFullPath(testRoot) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)),
                "ModuleTest writable paths escaped the sandbox.");
            Require(devPaths.ModuleDiscoveryDirectories.Count == 2 &&
                    testPaths.ModuleDiscoveryDirectories.Count == 1 &&
                    testPaths.ModuleDiscoveryDirectories[0] == testPaths.UserModulesDirectory,
                "Environment module discovery directories are incorrect.");
            foreach (var (sandboxEnvironment, sandboxPaths) in new[] { (dev, devPaths), (moduleTest, testPaths) })
            {
                sandboxPaths.EnsureDirectories();
                Require(Directory.Exists(sandboxPaths.UserModulesDirectory) &&
                        Directory.Exists(sandboxPaths.ModuleDataDirectory),
                    $"{sandboxEnvironment.Kind} directories were not created.");
                using var settings = new UserSettingsService(sandboxPaths.SettingsPath);
                await settings.UpdateAsync(value => value.Language = "zh-CN");
                Require(File.Exists(sandboxPaths.SettingsPath),
                    $"{sandboxEnvironment.Kind} settings were not written to the sandbox.");

                var fakeStore = new FakeStartupRegistrationStore();
                var registration = new WindowsStartupRegistrationService(settings, fakeStore, sandboxEnvironment);
                Require(!registration.IsAvailable && !(await registration.GetStateAsync()).IsRegistered,
                    $"{sandboxEnvironment.Kind} startup registration must be unavailable without reading the store.");
                var rejected = false;
                try { await registration.SetEnabledAsync(true); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected && fakeStore.Value is null && fakeStore.ReadCount == 0,
                    $"{sandboxEnvironment.Kind} startup registration was not rejected without store access.");
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Console.WriteLine("Execution environment isolation passed.");
    }

    private static async Task VerifyStartupInfrastructureAsync(DiscoveredModule helloModule)
    {
        Console.WriteLine("Verifying startup safety infrastructure...");
        Require(InstanceActivationProtocol.TryParse("Activate", out var activate) && activate == InstanceActivationMessage.Activate,
            "Activate protocol message was rejected.");
        Require(InstanceActivationProtocol.TryParse("StartupProbe", out var probe) && probe == InstanceActivationMessage.StartupProbe,
            "StartupProbe protocol message was rejected.");
        Require(!InstanceActivationProtocol.TryParse("run C:\\bad.exe", out _) &&
                !InstanceActivationProtocol.TryParse(new string('A', 64), out _),
            "Unsafe activation protocol input was accepted.");
        Require(WindowsStartupRegistrationService.BuildCommand(@"C:\Program Files\Qing Toolbox\QingToolbox.Shell.exe") ==
                "\"C:\\Program Files\\Qing Toolbox\\QingToolbox.Shell.exe\" --startup",
            "Startup command quoting is incorrect.");

        var pipeScope = $"Smoke.{Guid.NewGuid():N}";
        await using (var primary = SingleInstanceCoordinator.CreateForScope(pipeScope))
        {
            Require(primary.IsPrimary, "The first coordinator must own the current-user instance.");
            var received = new System.Collections.Concurrent.ConcurrentQueue<InstanceActivationMessage>();
            primary.MessageReceived += message => { received.Enqueue(message); return Task.CompletedTask; };
            await using var secondary = SingleInstanceCoordinator.CreateForScope(pipeScope);
            var delayedSend = secondary.SendAsync(InstanceActivationMessage.Activate);
            await Task.Delay(250);
            primary.StartServer();
            Require(!secondary.IsPrimary && await delayedSend &&
                    await secondary.SendAsync(InstanceActivationMessage.StartupProbe),
                "Pipe retry or acknowledgment failed.");
            await WaitUntilAsync(() => received.Count == 2);
            Require(received.Contains(InstanceActivationMessage.Activate) &&
                    received.Contains(InstanceActivationMessage.StartupProbe),
                "Pipe messages were not acknowledged and delivered.");
        }
        await using (var restarted = SingleInstanceCoordinator.CreateForScope(pipeScope))
            Require(restarted.IsPrimary, "Instance ownership was not released after shutdown.");

        var startupSession = new StartupSessionCoordinator(new ApplicationLaunchOptions(true));
        Require(startupSession.TryRequestManualActivation() && startupSession.TryRequestManualActivation(),
            "Active startup session rejected manual activation.");
        Require(startupSession.ManualActivationRequested &&
                startupSession.GetEffectivePresentation(StartupPresentationMode.FloatingBadge) == StartupPresentationMode.MainWindow,
            "Pending manual activation must be coalesced into one durable flag.");
        startupSession.PrepareForExit();
        startupSession.Complete();
        Require(startupSession.State == StartupSessionState.Exiting && !startupSession.TryRequestManualActivation(),
            "Exiting startup session accepted activation or returned to Ready.");
        startupSession.Dispose();
        startupSession.Dispose();

        await VerifyPipeFailureIsolationAsync();

        var root = Path.Combine(Path.GetTempPath(), $"QingToolbox-startup-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var settingsPath = Path.Combine(root, "settings.json");
            using var settingsService = new UserSettingsService(settingsPath);
            await settingsService.UpdateAsync(settings =>
            {
                settings.Language = "zh-CN";
                settings.LaunchAtLogin = true;
                settings.StartupModules =
                [
                    new StartupModuleAuthorization { ModuleId = " qing.hello ", ManifestSha256 = "aa", EntryAssemblySha256 = "bb" },
                    new StartupModuleAuthorization { ModuleId = "qing.hello", ManifestSha256 = "cc", EntryAssemblySha256 = "dd" }
                ];
            });
            var normalized = await settingsService.ReadAsync();
            Require(normalized.Language == "zh-CN" && normalized.LaunchAtLogin && normalized.StartupModules.Count == 1 &&
                    normalized.StartupModules[0].ManifestSha256 == "CC",
                "Startup settings did not preserve fields or normalize duplicate module ids.");
            var fakeStore = new FakeStartupRegistrationStore();
            var registration = new WindowsStartupRegistrationService(
                settingsService, fakeStore, ApplicationExecutionEnvironment.Production());
            await registration.SetEnabledAsync(true);
            Require(fakeStore.Value?.EndsWith(" --startup", StringComparison.Ordinal) == true,
                "Enabling startup did not write the owned registration value.");
            await registration.SetEnabledAsync(false);
            Require(fakeStore.Value is null, "Disabling startup did not remove the owned registration value.");

            var moduleRoot = Path.Combine(root, "module");
            Directory.CreateDirectory(moduleRoot);
            var manifest = Path.Combine(moduleRoot, "module.json");
            var entry = Path.Combine(moduleRoot, "entry.dll");
            await File.WriteAllTextAsync(manifest, "manifest-a");
            await File.WriteAllTextAsync(entry, "entry-a");
            var module = new DiscoveredModule
            {
                ModuleDirectory = moduleRoot,
                ManifestPath = manifest,
                Manifest = new ModuleManifest { Id = "test.module", Name = "Test", Version = "1.0", Entry = "entry.dll" }
            };
            var fingerprint = new ModuleStartupFingerprintService();
            var authorization = await fingerprint.CreateAuthorizationAsync(module);
            Require(authorization.FingerprintVersion == ModuleStartupFingerprintService.CurrentFingerprintVersion &&
                    authorization.PayloadFileCount == 2 && !string.IsNullOrWhiteSpace(authorization.PayloadSha256),
                "Complete payload authorization was not created.");
            Require(await fingerprint.MatchesAsync(module, authorization), "Stable module fingerprint did not match.");
            var legacyAuthorization = new StartupModuleAuthorization
            {
                ModuleId = module.Manifest.Id, Version = module.Manifest.Version,
                ManifestSha256 = authorization.ManifestSha256,
                EntryAssemblySha256 = authorization.EntryAssemblySha256
            };
            Require(!await fingerprint.MatchesAsync(module, legacyAuthorization),
                "Legacy entry-only authorization must require renewed confirmation.");
            File.SetLastWriteTimeUtc(entry, DateTime.UtcNow.AddMinutes(1));
            Require(await fingerprint.MatchesAsync(module, authorization), "Timestamp-only change altered the fingerprint.");
            await File.AppendAllTextAsync(manifest, "changed");
            Require(!await fingerprint.MatchesAsync(module, authorization), "Manifest content change was not detected.");
            var updatedAuthorization = await fingerprint.CreateAuthorizationAsync(module);
            await File.AppendAllTextAsync(entry, "changed");
            Require(!await fingerprint.MatchesAsync(module, updatedAuthorization), "Entry assembly content change was not detected.");

            await File.WriteAllTextAsync(manifest, "manifest-payload");
            await File.WriteAllTextAsync(entry, "entry-payload");
            var dependency = Path.Combine(moduleRoot, "dependency.dll");
            await File.WriteAllTextAsync(dependency, "dependency-a");
            var payloadAuthorization = await fingerprint.CreateAuthorizationAsync(module);
            File.SetLastWriteTimeUtc(dependency, DateTime.UtcNow.AddHours(1));
            Require(await fingerprint.MatchesAsync(module, payloadAuthorization),
                "Timestamp-only payload change altered authorization.");
            await File.AppendAllTextAsync(dependency, "changed");
            Require(!await fingerprint.MatchesAsync(module, payloadAuthorization),
                "Dependency content change was not detected.");
            await File.WriteAllTextAsync(dependency, "dependency-a");
            payloadAuthorization = await fingerprint.CreateAuthorizationAsync(module);
            var resource = Path.Combine(moduleRoot, "resource.json");
            await File.WriteAllTextAsync(resource, "{}");
            Require(!await fingerprint.MatchesAsync(module, payloadAuthorization),
                "Adding a payload file was not detected.");
            payloadAuthorization = await fingerprint.CreateAuthorizationAsync(module);
            File.Delete(resource);
            Require(!await fingerprint.MatchesAsync(module, payloadAuthorization),
                "Deleting a payload file was not detected.");
            var traversal = new DiscoveredModule
            {
                ModuleDirectory = moduleRoot, ManifestPath = manifest,
                Manifest = new ModuleManifest { Id = "bad", Name = "Bad", Version = "1", Entry = "../outside.dll" }
            };
            await File.WriteAllTextAsync(Path.Combine(root, "outside.dll"), "outside");
            var rejected = false;
            try { await fingerprint.CreateAuthorizationAsync(traversal); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "Entry path traversal was not rejected.");

            var linkPath = Path.Combine(moduleRoot, "linked-resource.json");
            try
            {
                File.CreateSymbolicLink(linkPath, Path.Combine(root, "outside.dll"));
                rejected = false;
                try { await fingerprint.CreateAuthorizationAsync(module); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "A symbolic-link payload was not rejected.");
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                Console.WriteLine("Symbolic-link creation is unavailable; reparse rejection remains enforced by contract.");
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("Startup safety infrastructure passed.");
    }

    private static async Task VerifyPipeFailureIsolationAsync()
    {
        var scope = $"PipeRecovery.{Guid.NewGuid():N}";
        await using var primary = SingleInstanceCoordinator.CreateForScope(scope);
        var invocation = 0;
        var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.MessageReceived += message =>
        {
            var current = Interlocked.Increment(ref invocation);
            if (current == 1) throw new InvalidOperationException("synchronous smoke failure");
            if (current == 2) return Task.FromException(new IOException("asynchronous smoke failure"));
            if (current == 3)
            {
                slowEntered.TrySetResult();
                return releaseSlow.Task;
            }
            return Task.CompletedTask;
        };
        primary.StartServer();
        await using var secondary = SingleInstanceCoordinator.CreateForScope(scope);
        Require(await secondary.SendAsync(InstanceActivationMessage.Activate),
            "Synchronous handler failure prevented acknowledgment.");
        await WaitUntilAsync(() => Volatile.Read(ref invocation) >= 1);
        Require(await secondary.SendAsync(InstanceActivationMessage.Activate),
            "Server stopped after synchronous handler failure.");
        await WaitUntilAsync(() => Volatile.Read(ref invocation) >= 2);

        var slowAcknowledgment = secondary.SendAsync(InstanceActivationMessage.Activate);
        await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(await slowAcknowledgment.WaitAsync(TimeSpan.FromSeconds(2)) && !releaseSlow.Task.IsCompleted,
            "Pipe acknowledgment waited for slow UI work.");
        releaseSlow.TrySetResult();

        Require(await SendRawPipeMessageAsync(scope, "unsupported\n") == SingleInstanceCoordinator.ErrorAcknowledgment,
            "Invalid pipe input did not return ERROR.");
        Require(await SendRawPipeMessageAsync(scope, new string('X', 64)) == SingleInstanceCoordinator.ErrorAcknowledgment,
            "Overlong non-terminated pipe input was not bounded and rejected.");
        Require(await SendRawPipeMessageAsync(scope, "Activate\r\n") == SingleInstanceCoordinator.ErrorAcknowledgment,
            "Embedded carriage return was accepted by the pipe protocol.");
        await SendRawPipeMessageAsync(scope, "Activate\n", disconnectBeforeAcknowledgment: true);
        Require(await secondary.SendAsync(InstanceActivationMessage.StartupProbe),
            "Client disconnect or asynchronous handler failure terminated the pipe server.");

        var disposeScope = $"PipeDispose.{Guid.NewGuid():N}";
        var disposingPrimary = SingleInstanceCoordinator.CreateForScope(disposeScope);
        var disposeHandlerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDisposeHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        disposingPrimary.MessageReceived += _ =>
        {
            disposeHandlerEntered.TrySetResult();
            return releaseDisposeHandler.Task;
        };
        disposingPrimary.StartServer();
        await using var disposingSecondary = SingleInstanceCoordinator.CreateForScope(disposeScope);
        Require(await disposingSecondary.SendAsync(InstanceActivationMessage.Activate),
            "Dispose-race activation was not acknowledged.");
        await disposeHandlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disposingPrimary.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        releaseDisposeHandler.TrySetResult();
        await disposingPrimary.DisposeAsync();
    }

    private static async Task<string?> SendRawPipeMessageAsync(
        string scope,
        string payload,
        bool disconnectBeforeAcknowledgment = false)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sid}\0{scope}")))[..16];
        using var client = new NamedPipeClientStream(
            ".", $"QingToolbox.{suffix}.Activation", PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(5000);
        var bytes = new UTF8Encoding(false).GetBytes(payload);
        await client.WriteAsync(bytes);
        await client.FlushAsync();
        if (disconnectBeforeAcknowledgment) return null;
        using var reader = new StreamReader(client, Encoding.UTF8, false, 128, leaveOpen: true);
        return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeStartupRegistrationStore : IStartupRegistrationStore
    {
        public string? Value { get; private set; }
        public int ReadCount { get; private set; }
        public string? Read() { ReadCount++; return Value; }
        public void Write(string command) => Value = command;
        public void Delete() => Value = null;
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

    private static void VerifyCloseBehaviorSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"QingToolbox-close-settings-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(root);
        try
        {
            using var service = new UserSettingsService(path);
            var missing = service.ReadAsync().GetAwaiter().GetResult();
            Require(missing.MainWindowCloseBehavior == MainWindowCloseBehavior.Ask,
                "Missing close behavior must default to Ask.");
            File.WriteAllText(path, "{\"Language\":\"zh-CN\",\"MainWindowCloseBehavior\":999}");
            var normalized = service.ReadAsync().GetAwaiter().GetResult();
            Require(normalized.MainWindowCloseBehavior == MainWindowCloseBehavior.Ask && normalized.Language == "zh-CN",
                "Invalid close behavior must normalize to Ask without losing other settings.");
            service.UpdateAsync(settings => settings.MainWindowCloseBehavior = MainWindowCloseBehavior.MinimizeToNotificationArea)
                .GetAwaiter().GetResult();
            Require(service.ReadAsync().GetAwaiter().GetResult().MainWindowCloseBehavior == MainWindowCloseBehavior.MinimizeToNotificationArea,
                "Close behavior must persist through the transactional settings update.");
            service.UpdateAsync(settings => settings.MainWindowCloseBehavior = MainWindowCloseBehavior.ExitApplication)
                .GetAwaiter().GetResult();
            Require(service.ReadAsync().GetAwaiter().GetResult().MainWindowCloseBehavior == MainWindowCloseBehavior.ExitApplication,
                "Exit close behavior must persist.");
            service.UpdateAsync(settings => settings.MainWindowCloseBehavior = MainWindowCloseBehavior.Ask)
                .GetAwaiter().GetResult();
            Require(service.ReadAsync().GetAwaiter().GetResult().MainWindowCloseBehavior == MainWindowCloseBehavior.Ask,
                "Ask must remain selectable after another close behavior.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void VerifyNotificationAreaLifecycleContracts(string repositoryRoot)
    {
        Console.WriteLine("Verifying notification-area lifecycle contracts...");
        var fake = new FakeNotificationAreaService();
        var opened = false;
        fake.OpenRequested = () => { opened = true; return Task.CompletedTask; };
        fake.OpenSettingsRequested = () => Task.CompletedTask;
        fake.FloatingBadgeRequested = () => Task.CompletedTask;
        fake.ExitRequested = () => Task.CompletedTask;
        Require(fake.OpenRequested is not null && fake.OpenSettingsRequested is not null &&
                fake.FloatingBadgeRequested is not null && fake.ExitRequested is not null,
            "Every notification-area callback must be configurable through the interface.");
        Require(fake.Initialize() && fake.IsAvailable, "A notification-area service must expose recoverability after initialization.");
        fake.OpenRequested!().GetAwaiter().GetResult();
        Require(opened, "The notification-area Open callback must be configurable through the interface.");
        fake.PrepareForExit();
        Require(fake.IsExiting && !fake.IsAvailable && !fake.Initialize(),
            "A notification-area service must reject recovery and reinitialization while exiting.");
        fake.Dispose();
        fake.Dispose();
        Require(fake.DisposeCount == 1, "Notification-area disposal must be idempotent.");

        var appXaml = File.ReadAllText(Path.Combine(repositoryRoot, "QingToolbox.Shell", "App.xaml"));
        Require(appXaml.Contains("ShutdownMode=\"OnExplicitShutdown\"", StringComparison.Ordinal),
            "The Shell must use explicit application shutdown.");
        Console.WriteLine("Notification-area lifecycle contracts passed.");
    }

    private static void VerifyExitFailureIsolation()
    {
        Console.WriteLine("Verifying exit failure isolation...");
        for (var failingStage = 0; failingStage < 6; failingStage++)
        {
            var completed = new List<int>();
            var failures = new List<string>();
            var shutdownCount = 0;
            var stages = Enumerable.Range(0, 6).Select(index => new ExitCleanupStage(
                $"stage-{index}",
                () =>
                {
                    if (index == failingStage) throw new InvalidOperationException("simulated");
                    completed.Add(index);
                    return Task.CompletedTask;
                }));
            ExitCleanupPipeline.RunAsync(
                stages,
                () => shutdownCount++,
                (stage, _) => failures.Add(stage)).GetAwaiter().GetResult();
            Require(shutdownCount == 1, "Every failed exit stage must still reach shutdown exactly once.");
            Require(completed.Count == 5 && failures.SequenceEqual([$"stage-{failingStage}"]),
                "A failed exit stage must not prevent remaining cleanup stages.");
        }

        var finalShutdownAttempts = 0;
        var task = ExitCleanupPipeline.RunAsync(
            [new ExitCleanupStage("cleanup", () => throw new InvalidOperationException("simulated"))],
            () => { finalShutdownAttempts++; throw new InvalidOperationException("simulated shutdown"); },
            (_, _) => { });
        task.GetAwaiter().GetResult();
        Require(task.IsCompletedSuccessfully && finalShutdownAttempts == 1,
            "Observed cleanup and shutdown failures must not leave a permanently faulted exit task.");
        Console.WriteLine("Exit failure isolation passed.");
    }

    private sealed class FakeNotificationAreaService : INotificationAreaIcon
    {
        private bool _disposed;
        public bool IsAvailable { get; private set; }
        public bool IsExiting { get; private set; }
        public int DisposeCount { get; private set; }
        public Func<Task>? OpenRequested { get; set; }
        public Func<Task>? OpenSettingsRequested { get; set; }
        public Func<Task>? FloatingBadgeRequested { get; set; }
        public Func<Task>? ExitRequested { get; set; }
        public bool Initialize()
        {
            if (_disposed || IsExiting) return false;
            return IsAvailable = true;
        }
        public void PrepareForExit() { IsExiting = true; IsAvailable = false; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeCount++;
            PrepareForExit();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
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
