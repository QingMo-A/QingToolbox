using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Localization;
using QingToolbox.Core.Runtime;
using QingToolbox.Core.Settings;
using QingToolbox.Core.Updates;
using QingToolbox.ModuleLoader;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;

namespace QingToolbox.DevTools.ModuleUpdateRuntimeAdapterSmokeTest;

internal static class Program
{
    private const string ModuleAlpha = "probe.runtime-alpha";
    private const string ModuleBeta = "probe.runtime-beta";
    private const string ProbeVersion = "1.0.0";
    private const string ProbeAssemblyName = "QingToolbox.DevTools.ModuleUpdateRuntimeAdapterProbe.dll";
    private const string LifecycleFileName = "runtime-adapter-lifecycle.tsv";

    [STAThread]
    public static int Main()
    {
        var exitCode = 1;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/QingToolbox.Shell;component/Resources/ShellTheme.xaml",
                UriKind.Absolute)
        });

        application.Startup += async (_, _) =>
        {
            try
            {
                await RunAsync();
                Console.WriteLine("Module update runtime adapter smoke test passed.");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
            }
            finally
            {
                application.Shutdown(exitCode);
            }
        };

        application.Run();
        return exitCode;
    }

    private static async Task RunAsync()
    {
        var uiThreadId = Environment.CurrentManagedThreadId;
        await using var fixture = await RuntimeFixture.CreateAsync();

        Console.WriteLine("Verifying initial state and startup authorization source...");
        await RequireStateAsync(fixture.Coordinator, ModuleAlpha, new(false, false, false, true));
        await RequireStateAsync(fixture.Coordinator, ModuleBeta, new(false, false, false, false));

        Console.WriteLine("Verifying loaded-inactive state, idempotence, and bounded ALC reclamation...");
        var loadedInactive = new ModuleUpdateRuntimeState(false, false, true, true);
        Require(await fixture.Coordinator.RestorePreviousRuntimeStateAsync(
            ModuleAlpha, loadedInactive, CancellationToken.None), "Loaded-inactive restore failed.");
        var collectible = RequireSnapshot(await fixture.Runtime.GetSnapshotAsync(ModuleAlpha), ModuleAlpha);
        Require(await fixture.Coordinator.RestorePreviousRuntimeStateAsync(
            ModuleAlpha, loadedInactive, CancellationToken.None), "Repeated loaded-inactive restore failed.");
        Require(RequireSnapshot(await fixture.Runtime.GetSnapshotAsync(ModuleAlpha), ModuleAlpha)
                    .LoadContextGeneration == collectible.LoadContextGeneration,
            "Repeated restore created an unnecessary load-context generation.");
        await fixture.Runtime.ActivateAsync(ModuleAlpha);
        Require(await fixture.Coordinator.DeactivateAsync(ModuleAlpha, CancellationToken.None),
            "Alpha deactivation failed.");
        Require(await fixture.Coordinator.DeactivateAsync(ModuleAlpha, CancellationToken.None),
            "Repeated alpha deactivation was not idempotent.");
        var collectibleWeakReference = collectible.CurrentLoadContext!;
        Require(await fixture.Coordinator.UnloadAsync(ModuleAlpha, CancellationToken.None),
            "Alpha unload failed.");
        Require(await fixture.Coordinator.UnloadAsync(ModuleAlpha, CancellationToken.None),
            "Repeated alpha unload was not idempotent.");
        Require(await fixture.Coordinator.VerifyUnloadedAsync(ModuleAlpha, CancellationToken.None),
            "Bounded alpha unload verification failed.");
        Require(await fixture.Coordinator.VerifyUnloadedAsync(ModuleAlpha, CancellationToken.None),
            "Repeated unload verification was not idempotent.");
        Require(!collectibleWeakReference.IsAlive,
            "The collectible alpha load context remained alive.");

        var unloadedIntent = new ModuleUpdateRuntimeState(false, false, false, true);
        Require(await fixture.Coordinator.RestorePreviousRuntimeStateAsync(
            ModuleAlpha, unloadedIntent, CancellationToken.None), "Unloaded restore failed.");
        Require(await fixture.Coordinator.RestorePreviousRuntimeStateAsync(
            ModuleAlpha, unloadedIntent, CancellationToken.None), "Repeated unloaded restore failed.");

        Console.WriteLine("Restoring two independent runtimes and host-owned WPF windows...");
        var desiredAlpha = new ModuleUpdateRuntimeState(false, true, true, true);
        var desiredBeta = new ModuleUpdateRuntimeState(false, true, true, false);
        Require(await fixture.Coordinator.RestorePreviousRuntimeStateAsync(
            ModuleAlpha, desiredAlpha, CancellationToken.None), "Alpha window restore failed.");
        Require(await fixture.Coordinator.RestorePreviousRuntimeStateAsync(
            ModuleBeta, desiredBeta, CancellationToken.None), "Beta window restore failed.");
        var alphaWindowGeneration = RequireSnapshot(
            await fixture.Runtime.GetSnapshotAsync(ModuleAlpha), ModuleAlpha);
        var betaWindowGeneration = RequireSnapshot(
            await fixture.Runtime.GetSnapshotAsync(ModuleBeta), ModuleBeta);
        Require(await fixture.Coordinator.RestorePreviousRuntimeStateAsync(
            ModuleAlpha, desiredAlpha, CancellationToken.None), "Repeated alpha window restore failed.");
        Require(RequireSnapshot(await fixture.Runtime.GetSnapshotAsync(ModuleAlpha), ModuleAlpha)
                    .LoadContextGeneration == alphaWindowGeneration.LoadContextGeneration,
            "Repeated window restore created another alpha runtime generation.");
        fixture.Windows.OpenWindow(
            ModuleAlpha, "Runtime Alpha", new Border { Child = new TextBlock { Text = "alpha" } },
            fixture.HostWindow);
        fixture.Windows.OpenWindow(
            ModuleBeta, "Runtime Beta", new Border { Child = new TextBlock { Text = "beta" } },
            fixture.HostWindow);
        await RequireStateAsync(fixture.Coordinator, ModuleAlpha, desiredAlpha with { HasWindows = true });
        await RequireStateAsync(fixture.Coordinator, ModuleBeta, desiredBeta with { HasWindows = true });
        await RequireVersionConsistencyAsync(fixture, ModuleAlpha, alphaWindowGeneration);
        await RequireVersionConsistencyAsync(fixture, ModuleBeta, betaWindowGeneration);

        Console.WriteLine("Closing alpha from a background thread through the Dispatcher...");
        var backgroundThreadId = 0;
        var closed = await Task.Run(async () =>
        {
            backgroundThreadId = Environment.CurrentManagedThreadId;
            return await fixture.Coordinator.RequestCloseWindowsAsync(ModuleAlpha, CancellationToken.None);
        });
        Require(backgroundThreadId != uiThreadId && closed,
            "Background Dispatcher window close failed.");
        Require(await fixture.Coordinator.RequestCloseWindowsAsync(ModuleAlpha, CancellationToken.None),
            "Repeated alpha window close was not idempotent.");
        await RequireBetaUnchangedAsync(fixture, betaWindowGeneration);
        Require(await fixture.Coordinator.DeactivateAsync(ModuleAlpha, CancellationToken.None),
            "Window generation deactivation failed.");
        Require(await fixture.Coordinator.UnloadAsync(ModuleAlpha, CancellationToken.None),
            "Window generation unload request failed.");

        Console.WriteLine("Verifying recovery gate ordering and module-scoped blocking...");
        await VerifyRecoveryGateAsync();

        Console.WriteLine("Verifying startup authorization bytes remained unchanged...");
        var finalSettingsBytes = await File.ReadAllBytesAsync(fixture.SettingsPath);
        Require(fixture.InitialSettingsBytes.AsSpan().SequenceEqual(finalSettingsBytes),
            "Runtime coordination changed the startup authorization settings file.");
        var finalSettings = await fixture.Settings.ReadAsync();
        Require(finalSettings.StartupModules.Count == 1 &&
                finalSettings.StartupModules[0].ModuleId == ModuleAlpha &&
                finalSettings.StartupModules[0].ManifestSha256 == fixture.InitialAuthorization.ManifestSha256 &&
                finalSettings.StartupModules[0].PayloadSha256 == fixture.InitialAuthorization.PayloadSha256 &&
                finalSettings.StartupModules[0].ActivateOnStartup == fixture.InitialAuthorization.ActivateOnStartup,
            "Runtime coordination changed startup authorization semantics.");
    }

    private static async Task VerifyRecoveryGateAsync()
    {
        var pendingGate = new ModuleTransactionRecoveryGate();
        var pending = pendingGate.Consumer.EnterExecutionAsync(ModuleAlpha).AsTask();
        await Task.Delay(25);
        Require(!pending.IsCompleted, "Execution was not held while recovery was pending.");
        pendingGate.CompleteRecovery([], false);
        await using (var releasedLease = await pending) { }

        var scopedGate = new ModuleTransactionRecoveryGate();
        scopedGate.CompleteRecovery([ModuleAlpha], false);
        try
        {
            await using var blockedLease = await scopedGate.Consumer.EnterExecutionAsync(ModuleAlpha);
            throw new InvalidOperationException("RecoveryRequired alpha execution was not blocked.");
        }
        catch (ModuleExecutionBlockedException exception)
        {
            Require(exception.ModuleId == ModuleAlpha, "The wrong module was blocked.");
        }
        await using (var betaLease = await scopedGate.Consumer.EnterExecutionAsync(ModuleBeta)) { }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FirstGenerationResult RunFirstGenerationLifecycle(
        RuntimeFixture fixture,
        int uiThreadId) =>
        RunFirstGenerationLifecycleAsync(fixture, uiThreadId).GetAwaiter().GetResult();

    private static async Task<FirstGenerationResult> RunFirstGenerationLifecycleAsync(
        RuntimeFixture fixture,
        int uiThreadId)
    {

        Console.WriteLine("Verifying initial state and startup authorization source...");
        await RequireStateAsync(
            fixture.Coordinator,
            ModuleAlpha,
            new(false, false, false, true));
        await RequireStateAsync(
            fixture.Coordinator,
            ModuleBeta,
            new(false, false, false, false));

        var desiredAlpha = new ModuleUpdateRuntimeState(true, true, true, true);
        var desiredBeta = new ModuleUpdateRuntimeState(true, true, true, false);

        Console.WriteLine("Restoring two independent real module runtimes and WPF windows...");
        Require(await fixture.Coordinator.RestorePreviousRuntimeStateAsync(
            ModuleAlpha, desiredAlpha, CancellationToken.None),
            "Alpha runtime intent was not restored.");
        Require(await fixture.Coordinator.RestorePreviousRuntimeStateAsync(
            ModuleBeta, desiredBeta, CancellationToken.None),
            "Beta runtime intent was not restored.");
        await RequireStateAsync(fixture.Coordinator, ModuleAlpha, desiredAlpha);
        await RequireStateAsync(fixture.Coordinator, ModuleBeta, desiredBeta);

        var alphaFirst = RequireSnapshot(
            await fixture.Runtime.GetSnapshotAsync(ModuleAlpha), ModuleAlpha);
        var betaFirst = RequireSnapshot(
            await fixture.Runtime.GetSnapshotAsync(ModuleBeta), ModuleBeta);
        Require(alphaFirst.CurrentLoadContext is { IsAlive: true },
            "Alpha collectible load context was not alive after load.");
        Require(betaFirst.CurrentLoadContext is { IsAlive: true },
            "Beta collectible load context was not alive after load.");
        Require(alphaFirst.LoadContextGeneration != betaFirst.LoadContextGeneration,
            "Two modules shared one load-context generation.");
        await RequireVersionConsistencyAsync(fixture, ModuleAlpha, alphaFirst);
        await RequireVersionConsistencyAsync(fixture, ModuleBeta, betaFirst);
        RequireLifecycleCounts(fixture, ModuleAlpha,
            ("Load", 1), ("Activate", 1), ("CreateView", 1));
        RequireLifecycleCounts(fixture, ModuleBeta,
            ("Load", 1), ("Activate", 1), ("CreateView", 1));
        Require(
            fixture.ReadLifecycle(ModuleAlpha)
                .Single(item => item.Operation == "CreateView").ThreadId == uiThreadId,
            "Alpha WPF view was not created on the application Dispatcher thread.");

        Console.WriteLine("Closing alpha from a background thread through the Dispatcher...");
        var backgroundThreadId = 0;
        var closed = await Task.Run(async () =>
        {
            backgroundThreadId = Environment.CurrentManagedThreadId;
            return await fixture.Coordinator.RequestCloseWindowsAsync(
                ModuleAlpha, CancellationToken.None);
        });
        Require(backgroundThreadId != uiThreadId,
            "Background close probe unexpectedly ran on the UI thread.");
        Require(closed, "Background window close failed.");
        Require(await fixture.Coordinator.RequestCloseWindowsAsync(
            ModuleAlpha, CancellationToken.None),
            "Repeated window close was not idempotent.");
        await RequireStateAsync(
            fixture.Coordinator,
            ModuleAlpha,
            new(false, true, true, true));
        await RequireStateAsync(fixture.Coordinator, ModuleBeta, desiredBeta);
        await RequireBetaUnchangedAsync(fixture, betaFirst);

        Console.WriteLine("Verifying idempotent deactivate and unload with module isolation...");
        Require(await fixture.Coordinator.DeactivateAsync(ModuleAlpha, CancellationToken.None),
            "Alpha deactivation failed.");
        Require(await fixture.Coordinator.DeactivateAsync(ModuleAlpha, CancellationToken.None),
            "Repeated alpha deactivation was not idempotent.");
        await RequireStateAsync(
            fixture.Coordinator,
            ModuleAlpha,
            new(false, false, true, true));
        RequireLifecycleCounts(fixture, ModuleAlpha, ("Deactivate", 1));
        await RequireBetaUnchangedAsync(fixture, betaFirst);

        var alphaWeakReference = alphaFirst.CurrentLoadContext!;
        Require(await fixture.Coordinator.UnloadAsync(ModuleAlpha, CancellationToken.None),
            "Alpha unload failed.");
        Require(await fixture.Coordinator.UnloadAsync(ModuleAlpha, CancellationToken.None),
            "Repeated alpha unload was not idempotent.");
        await RequireStateAsync(
            fixture.Coordinator,
            ModuleAlpha,
            new(false, false, false, true));
        RequireLifecycleCounts(fixture, ModuleAlpha, ("Unload", 1), ("Dispose", 1));
        await RequireBetaUnchangedAsync(fixture, betaFirst);
        return new(
            alphaWeakReference,
            alphaFirst.LoadContextGeneration,
            betaFirst,
            desiredAlpha,
            desiredBeta);
    }

    private static async Task RequireBetaUnchangedAsync(
        RuntimeFixture fixture,
        ModuleRuntimeSnapshot betaFirst)
    {
        var current = RequireSnapshot(
            await fixture.Runtime.GetSnapshotAsync(ModuleBeta),
            ModuleBeta);
        Require(current.HasRuntimeRegistration && current.IsActive,
            "Alpha lifecycle work changed beta runtime state.");
        Require(current.LoadContextGeneration == betaFirst.LoadContextGeneration,
            "Alpha lifecycle work replaced beta's load context.");
        RequireLifecycleCounts(fixture, ModuleBeta,
            ("Load", 1), ("Activate", 1), ("CreateView", 0),
            ("Deactivate", 0), ("Unload", 0), ("Dispose", 0));
    }

    private static async Task RequireVersionConsistencyAsync(
        RuntimeFixture fixture,
        string moduleId,
        ModuleRuntimeSnapshot snapshot)
    {
        var diagnostic = fixture.Coordinator.GetDiagnostic(moduleId)
            ?? throw new InvalidOperationException($"No diagnostic exists for '{moduleId}'.");
        Require(snapshot.Version == ProbeVersion,
            $"{moduleId} runtime manifest version mismatch: {snapshot.Version}.");
        Require(snapshot.RuntimeAssemblyInformationalVersion == ProbeVersion,
            $"{moduleId} runtime assembly version mismatch: {snapshot.RuntimeAssemblyInformationalVersion}.");
        Require(diagnostic.Version == snapshot.Version &&
                diagnostic.RuntimeAssemblyInformationalVersion == snapshot.RuntimeAssemblyInformationalVersion,
            $"{moduleId} diagnostic version did not match the live runtime.");
        Require(await fixture.Windows.IsWindowOpenAsync(moduleId),
            $"{moduleId} runtime-version check did not have its real WPF window open.");
        Require(diagnostic.ProgramDirectoryIdentity.Length == 64 &&
                diagnostic.ProgramDirectoryIdentity.All(char.IsAsciiHexDigit),
            $"{moduleId} program directory identity was not a SHA256 value.");
    }

    private static async Task RequireStateAsync(
        ModuleUpdateRuntimeCoordinator coordinator,
        string moduleId,
        ModuleUpdateRuntimeState expected)
    {
        var actual = await coordinator.GetRuntimeStateAsync(moduleId, CancellationToken.None);
        Require(actual == expected,
            $"{moduleId} state mismatch. Expected {expected}; actual {actual}.");
    }

    private static ModuleRuntimeSnapshot RequireSnapshot(
        ModuleRuntimeSnapshot? snapshot,
        string moduleId) =>
        snapshot ?? throw new InvalidOperationException($"No runtime snapshot exists for '{moduleId}'.");

    private static void RequireLifecycleCounts(
        RuntimeFixture fixture,
        string moduleId,
        params (string Operation, int Count)[] expected)
    {
        var lifecycle = fixture.ReadLifecycle(moduleId);
        foreach (var (operation, count) in expected)
        {
            var actual = lifecycle.Count(item => item.Operation == operation);
            Require(actual == count,
                $"{moduleId} lifecycle '{operation}' count mismatch. Expected {count}; actual {actual}.");
        }

        Require(lifecycle.All(item => item.Version == ProbeVersion),
            $"{moduleId} emitted a lifecycle event from an unexpected runtime version.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record LifecycleEvent(string Operation, string Version, int ThreadId);

    private sealed record FirstGenerationResult(
        WeakReference AlphaLoadContext,
        long AlphaGeneration,
        ModuleRuntimeSnapshot BetaSnapshot,
        ModuleUpdateRuntimeState DesiredAlpha,
        ModuleUpdateRuntimeState DesiredBeta);

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private RuntimeFixture(
            string root,
            string userModulesRoot,
            string moduleDataRoot,
            string settingsPath,
            UserSettingsService settings,
            ModuleRuntimeManager runtime,
            ModuleWindowManager windows,
            ModuleUpdateRuntimeCoordinator coordinator,
            SessionLogService sessionLog,
            Window hostWindow,
            StartupModuleAuthorization initialAuthorization,
            byte[] initialSettingsBytes)
        {
            Root = root;
            UserModulesRoot = userModulesRoot;
            ModuleDataRoot = moduleDataRoot;
            SettingsPath = settingsPath;
            Settings = settings;
            Runtime = runtime;
            Windows = windows;
            Coordinator = coordinator;
            SessionLog = sessionLog;
            HostWindow = hostWindow;
            InitialAuthorization = initialAuthorization;
            InitialSettingsBytes = initialSettingsBytes;
        }

        public string Root { get; }
        public string UserModulesRoot { get; }
        public string ModuleDataRoot { get; }
        public string SettingsPath { get; }
        public UserSettingsService Settings { get; }
        public ModuleRuntimeManager Runtime { get; }
        public ModuleWindowManager Windows { get; }
        public ModuleUpdateRuntimeCoordinator Coordinator { get; }
        public SessionLogService SessionLog { get; }
        public Window HostWindow { get; }
        public StartupModuleAuthorization InitialAuthorization { get; }
        public byte[] InitialSettingsBytes { get; }

        public static async Task<RuntimeFixture> CreateAsync()
        {
            var repositoryRoot = FindRepositoryRoot();
            var profile = $"runtime-adapter-{Environment.ProcessId}-{Guid.NewGuid():N}"[..48];
            var environment = ApplicationExecutionEnvironment.Sandbox(
                ApplicationEnvironmentKind.ModuleTest,
                profile,
                repositoryRoot);
            var paths = new ApplicationPaths(environment);
            paths.EnsureDirectories();
            var root = environment.SandboxRoot!;
            var userModulesRoot = paths.UserModulesDirectory;
            var moduleDataRoot = paths.ModuleDataDirectory;
            var settingsPath = paths.SettingsPath;

            var probeAssembly = Path.Combine(
                AppContext.BaseDirectory,
                "ProbePayload",
                ProbeAssemblyName);
            if (!File.Exists(probeAssembly))
            {
                throw new FileNotFoundException("The build-only runtime adapter probe is missing.", probeAssembly);
            }

            await InstallModuleAsync(userModulesRoot, ModuleAlpha, "Runtime Alpha", probeAssembly);
            await InstallModuleAsync(userModulesRoot, ModuleBeta, "Runtime Beta", probeAssembly);

            var settings = new UserSettingsService(settingsPath);
            var localization = new LocalizationManager(settings);
            var manifestReader = new ModuleManifestReader();
            var manifestValidator = new ModuleManifestValidator();
            var fingerprint = new ModuleStartupFingerprintService();
            var runtime = new ModuleRuntimeManager(new InProcessModuleLoader(localization));
            var windows = new ModuleWindowManager(localization);
            var sessionLog = new SessionLogService(paths, TimeProvider.System);
            var hostWindow = new Window
            {
                Title = "Runtime adapter smoke-test host",
                Width = 1,
                Height = 1,
                Left = -10_000,
                Top = -10_000,
                Opacity = 0,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            Application.Current.MainWindow = hostWindow;
            hostWindow.Show();
            var coordinator = new ModuleUpdateRuntimeCoordinator(
                runtime,
                windows,
                settings,
                manifestReader,
                manifestValidator,
                fingerprint,
                paths,
                localization,
                sessionLog);

            var alphaModule = await ReadDiscoveredModuleAsync(
                userModulesRoot, ModuleAlpha, manifestReader, manifestValidator);
            var authorization = await fingerprint.CreateAuthorizationAsync(alphaModule);
            authorization.ActivateOnStartup = false;
            await settings.UpdateAsync(value => value.StartupModules.Add(authorization));
            var initialSettingsBytes = await File.ReadAllBytesAsync(settingsPath);

            return new(
                root,
                userModulesRoot,
                moduleDataRoot,
                settingsPath,
                settings,
                runtime,
                windows,
                coordinator,
                sessionLog,
                hostWindow,
                authorization,
                initialSettingsBytes);
        }

        public IReadOnlyList<LifecycleEvent> ReadLifecycle(string moduleId)
        {
            var path = Path.Combine(ModuleDataRoot, moduleId, LifecycleFileName);
            if (!File.Exists(path))
            {
                return [];
            }

            return File.ReadAllLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line =>
                {
                    var parts = line.Split('\t');
                    if (parts.Length != 3 || !int.TryParse(parts[2], out var threadId))
                    {
                        throw new InvalidDataException($"Invalid probe lifecycle record: {line}");
                    }

                    return new LifecycleEvent(parts[0], parts[1], threadId);
                })
                .ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var moduleId in new[] { ModuleAlpha, ModuleBeta })
            {
                try
                {
                    await Coordinator.RequestCloseWindowsAsync(moduleId, CancellationToken.None);
                    await Coordinator.DeactivateAsync(moduleId, CancellationToken.None);
                    await Coordinator.UnloadAsync(moduleId, CancellationToken.None);
                    await Coordinator.VerifyUnloadedAsync(moduleId, CancellationToken.None);
                }
                catch
                {
                    // Continue releasing the remaining test-only resources.
                }
            }

            HostWindow.Close();
            if (ReferenceEquals(Application.Current.MainWindow, HostWindow))
            {
                Application.Current.MainWindow = null;
            }

            await Application.Current.Dispatcher.InvokeAsync(
                static () => { },
                DispatcherPriority.ApplicationIdle);

            try
            {
                await Runtime.DisposeAsync();
                CollectReleasedLoadContexts();
            }
            finally
            {
                SessionLog.Dispose();
                Settings.Dispose();
            }

            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Test fixture cleanup degraded: {exception.GetType().Name}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void CollectReleasedLoadContexts()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static string FindRepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")) &&
                    File.Exists(Path.Combine(
                        current.FullName,
                        "QingToolbox.Shell",
                        "QingToolbox.Shell.csproj")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new DirectoryNotFoundException("The QingToolbox repository root could not be located.");
        }

        private static async Task InstallModuleAsync(
            string userModulesRoot,
            string moduleId,
            string displayName,
            string probeAssembly)
        {
            var directory = Path.Combine(userModulesRoot, moduleId);
            Directory.CreateDirectory(directory);
            File.Copy(probeAssembly, Path.Combine(directory, ProbeAssemblyName));
            var manifest = new
            {
                id = moduleId,
                name = displayName,
                description = "Runtime adapter smoke-test probe",
                version = ProbeVersion,
                entry = ProbeAssemblyName,
                runtimeType = "InProcess",
                loadMode = "Manual"
            };
            await File.WriteAllTextAsync(
                Path.Combine(directory, "module.json"),
                JsonSerializer.Serialize(manifest));
        }

        private static async Task<DiscoveredModule> ReadDiscoveredModuleAsync(
            string userModulesRoot,
            string moduleId,
            ModuleManifestReader reader,
            ModuleManifestValidator validator)
        {
            var directory = Path.Combine(userModulesRoot, moduleId);
            var manifestPath = Path.Combine(directory, "module.json");
            var manifest = await reader.ReadAsync(manifestPath)
                ?? throw new InvalidDataException($"Manifest for '{moduleId}' could not be read.");
            var errors = validator.Validate(manifest, directory, manifestPath);
            if (errors.Count != 0)
            {
                throw new InvalidDataException(string.Join(
                    Environment.NewLine,
                    errors.Select(item => item.Message)));
            }

            return new DiscoveredModule
            {
                Manifest = manifest,
                ModuleDirectory = directory,
                ManifestPath = manifestPath,
                State = ModuleState.NotLoaded,
                Errors = []
            };
        }
    }
}
