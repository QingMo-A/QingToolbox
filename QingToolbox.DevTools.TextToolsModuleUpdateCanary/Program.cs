using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Runtime;
using QingToolbox.Core.Localization;
using QingToolbox.Core.Settings;
using QingToolbox.Core.Updates;
using QingToolbox.ModuleLoader;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;

namespace QingToolbox.DevTools.TextToolsModuleUpdateCanary;

internal static class Program
{
    private const string ModuleId = "qing.texttools";
    private const string BatchModuleId = "qing.texttools.batch-probe";
    private const string VersionOne = "1.0.0";
    private const string VersionTwo = "2.0.0";
    private const string PinnedSourceCommit = "bc0e57b5a77e3526de157d92a3d300bf3d267e8b";

    [STAThread]
    private static int Main(string[] args)
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
                var options = CanaryOptions.Parse(args);
                var packageDirectory = Path.Combine(
                    options.ArtifactsRoot,
                    options.EnvironmentKind.ToString().ToLowerInvariant());
                Directory.CreateDirectory(packageDirectory);

                var versionTwoPackage = TextToolsPackageBuilder.Create(
                    options.VersionTwoOutput,
                    packageDirectory,
                    VersionTwo,
                    "TextTools.Canary.2.0.0.qmod");

                foreach (var scenario in Enum.GetValues<CanaryScenario>())
                {
                    await RunScenarioAsync(
                        options,
                        scenario,
                        options.VersionOneOutput,
                        versionTwoPackage);
                }

                Console.WriteLine(
                    $"TextTools module-update canary passed for {options.EnvironmentKind}: " +
                    "commit, rollback, and RecoveryRequired.");
                exitCode = 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"TextTools module-update canary failed: {exception}");
            }
            finally
            {
                application.Shutdown(exitCode);
            }
        };
        application.Run();
        return exitCode;
    }

    private static async Task RunScenarioAsync(
        CanaryOptions options,
        CanaryScenario scenario,
        string versionOneOutput,
        PackageArtifact versionTwoPackage)
    {
        var profileName = $"tt-canary-{options.RunId}-{ScenarioSlug(scenario)}";
        var environment = ApplicationExecutionEnvironment.Sandbox(
            options.EnvironmentKind,
            profileName,
            options.RepositoryRoot);
        var paths = new ApplicationPaths(environment);
        ValidateScenarioPaths(environment, paths, profileName);
        Exception? scenarioFailure = null;

        Console.WriteLine($"[{options.EnvironmentKind}/{scenario}] starting isolated profile {profileName}...");
        try
        {
            paths.EnsureDirectories();
            await RejectDevelopmentShadowAsync(environment, paths);

            await using var coordinator = new CanaryRuntimeCoordinator(paths, PinnedSourceCommit);
            await using var staging = new QmodPackageStagingService(
                paths.QmodStagingDirectory,
                TimeProvider.System,
                environment.Kind.ToString(),
                paths.UserModulesDirectory);
            await using var transactions = new ModuleUpdateTransactionService(
                environment.Kind.ToString(),
                paths.UserModulesDirectory,
                paths.ModuleTransactionsDirectory,
                ModuleUpdateIdentity.ModuleApiVersion,
                staging,
                coordinator,
                item => Console.WriteLine(
                    $"[{options.EnvironmentKind}/{scenario}] " +
                    $"tx={item.TransactionIdPrefix} state={item.State} event={item.EventName}"));
            var gate = new ModuleTransactionRecoveryGate();
            gate.CompleteRecovery([], false);
            var gatedTransactions = new GatedModuleUpdateTransactionCoordinator(transactions, gate);

            await InstallAndRunVersionOneAsync(
                paths,
                coordinator,
                versionOneOutput);

            switch (scenario)
            {
                case CanaryScenario.Success:
                    await RunSuccessAsync(
                        paths,
                        coordinator,
                        staging,
                        gatedTransactions,
                        versionTwoPackage);
                    break;
                case CanaryScenario.Rollback:
                    await RunRollbackAsync(
                        paths,
                        coordinator,
                        staging,
                        gatedTransactions,
                        versionTwoPackage);
                    break;
                case CanaryScenario.RecoveryRequired:
                    await RunRecoveryRequiredAsync(
                        paths,
                        coordinator,
                        staging,
                        gatedTransactions,
                        transactions,
                        gate,
                        versionTwoPackage);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }

            Console.WriteLine($"[{options.EnvironmentKind}/{scenario}] passed.");
        }
        catch (Exception exception)
        {
            scenarioFailure = exception;
            throw;
        }
        finally
        {
            try
            {
                DeleteOwnedSandbox(environment, profileName);
            }
            catch (Exception cleanupException) when (scenarioFailure is not null)
            {
                Console.Error.WriteLine(
                    $"Canary profile cleanup also failed: {cleanupException.Message}");
            }
        }
    }

    private static async Task InstallAndRunVersionOneAsync(
        ApplicationPaths paths,
        CanaryRuntimeCoordinator coordinator,
        string versionOneOutput)
    {
        // Model the normal update precondition: a real v1 payload is already installed,
        // and the single transaction exercised by each scenario is the v1-to-v2 update.
        TextToolsPackageBuilder.SeedInstalled(
            versionOneOutput,
            paths.UserModulesDirectory,
            VersionOne);
        SeedBatchProbe(paths);
        AssertInstalledVersion(paths, VersionOne);
        await coordinator.VerifyEarlyExitPublicationAsync(ModuleId);
        await coordinator.LoadAndActivateInstalledAsync(ModuleId);
        await coordinator.AssertRuntimeAsync(ModuleId, VersionOne, "v1", active: true);
        await coordinator.VerifyWindowSuspendRestoreAsync(ModuleId);
        await coordinator.VerifyUnexpectedExitRecoveryAsync(ModuleId, VersionOne, "v1");
        await coordinator.VerifyMultiWorkerBatchAsync(ModuleId, BatchModuleId);
        AssertTransactionClean(paths);
    }

    private static void SeedBatchProbe(ApplicationPaths paths)
    {
        var source = Path.Combine(paths.UserModulesDirectory, ModuleId);
        var destination = Path.Combine(paths.UserModulesDirectory, BatchModuleId);
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        var manifestPath = Path.Combine(destination, "module.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException("Batch probe manifest is invalid.");
        manifest["id"] = BatchModuleId;
        manifest["name"] = "TextTools Batch Probe";
        File.WriteAllText(manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RunSuccessAsync(
        ApplicationPaths paths,
        CanaryRuntimeCoordinator coordinator,
        QmodPackageStagingService staging,
        GatedModuleUpdateTransactionCoordinator transactions,
        PackageArtifact package)
    {
        var result = await ExecuteAsync(staging, transactions, package, VersionOne);
        Require(
            result.Succeeded &&
            result.State == ModuleUpdateTransactionState.Committed &&
            result.FailureCode == ModuleUpdateTransactionFailureCode.None,
            $"v2 update did not commit: {Describe(result)}");

        AssertInstalledVersion(paths, VersionTwo);
        await coordinator.AssertRuntimeAsync(ModuleId, VersionTwo, "v2", active: true);
        AssertTransactionClean(paths);
    }

    private static async Task RunRollbackAsync(
        ApplicationPaths paths,
        CanaryRuntimeCoordinator coordinator,
        QmodPackageStagingService staging,
        GatedModuleUpdateTransactionCoordinator transactions,
        PackageArtifact package)
    {
        coordinator.ArmPromotedRestoreFailure(blockPromotedUnload: false);
        var result = await ExecuteAsync(staging, transactions, package, VersionOne);
        Require(
            !result.Succeeded &&
            result.RolledBack &&
            result.State == ModuleUpdateTransactionState.RolledBack &&
            result.FailureCode == ModuleUpdateTransactionFailureCode.RuntimeRestoreFailed,
            $"v2 update did not take the real rollback path: {Describe(result)}");

        AssertInstalledVersion(paths, VersionOne);
        await coordinator.AssertRuntimeAsync(ModuleId, VersionOne, "v1", active: true);
        AssertTransactionClean(paths);
    }

    private static async Task RunRecoveryRequiredAsync(
        ApplicationPaths paths,
        CanaryRuntimeCoordinator coordinator,
        QmodPackageStagingService staging,
        GatedModuleUpdateTransactionCoordinator transactions,
        ModuleUpdateTransactionService recoveryTransactions,
        ModuleTransactionRecoveryGate gate,
        PackageArtifact package)
    {
        coordinator.ArmPromotedRestoreFailure(blockPromotedUnload: true);
        var result = await ExecuteAsync(staging, transactions, package, VersionOne);
        Require(
            !result.Succeeded &&
            !result.RolledBack &&
            result.State == ModuleUpdateTransactionState.RecoveryRequired &&
            result.FailureCode == ModuleUpdateTransactionFailureCode.RecoveryRequired,
            $"v2 update did not enter RecoveryRequired: {Describe(result)}");

        AssertInstalledVersion(paths, VersionTwo);
        await coordinator.AssertRuntimeAsync(ModuleId, VersionTwo, "v2", active: false, hasWindows: false);
        AssertRecoveryArtifacts(paths, result.TransactionId);

        var recovery = await recoveryTransactions.RecoverAsync();
        Require(
            recovery.RecoveryRequired == 1 &&
            recovery.RecoveryRequiredModuleIds.SequenceEqual([ModuleId], StringComparer.Ordinal) &&
            recovery.Results.Count == 1 &&
            recovery.Results[0].State == ModuleUpdateTransactionState.RecoveryRequired,
            "Recovery scan did not preserve and report the module-scoped RecoveryRequired state.");
        foreach (var blockedModuleId in recovery.RecoveryRequiredModuleIds)
            gate.BlockModule(blockedModuleId, "RecoveryRequired");
        Require(!gate.Consumer.GetReadiness(ModuleId).CanExecute &&
                gate.Consumer.GetReadiness("qing.canary-neighbor").CanExecute,
            "RecoveryRequired did not block only the affected TextTools module.");
        AssertRecoveryArtifacts(paths, result.TransactionId);
    }

    private static async Task<ModuleUpdateTransactionResult> ExecuteAsync(
        QmodPackageStagingService staging,
        GatedModuleUpdateTransactionCoordinator transactions,
        PackageArtifact package,
        string localVersion)
    {
        var input = package.CreateStagingInput(localVersion);
        var stagingResult = await staging.StageAsync(input);
        Require(
            stagingResult.Succeeded,
            $"Staging {package.Version} failed: {stagingResult.FailureCode}");
        var attestation = await staging.AttestVerifiedStagingAsync(input)
            ?? throw new InvalidOperationException(
                $"Staging attestation for {package.Version} was unavailable.");
        return await transactions.ExecuteAsync(new ModuleUpdateTransactionInput(attestation));
    }

    private static async Task RejectDevelopmentShadowAsync(
        ApplicationExecutionEnvironment environment,
        ApplicationPaths paths)
    {
        if (environment.IsModuleTest)
        {
            Require(
                paths.ModuleDiscoveryDirectories.Count == 1 &&
                SamePath(paths.ModuleDiscoveryDirectories[0], paths.UserModulesDirectory),
                "ModuleTest discovery must be isolated to its profile UserModules directory.");
            return;
        }

        var scanner = new ModuleManifestScanner(
            new ModuleManifestReader(),
            new ModuleManifestValidator());
        foreach (var root in paths.ModuleDiscoveryDirectories.Where(
                     root => !SamePath(root, paths.UserModulesDirectory)))
        {
            var shadow = (await scanner.ScanAsync(root)).FirstOrDefault(
                module => module.Manifest.Id == ModuleId);
            Require(
                shadow is null,
                $"Development canary refused a shadowing TextTools module outside its profile: {root}");
        }
    }

    private static void ValidateScenarioPaths(
        ApplicationExecutionEnvironment environment,
        ApplicationPaths paths,
        string profileName)
    {
        Require(!environment.IsProduction, "The canary must never run in Production.");
        var sandboxRoot = Path.GetFullPath(environment.SandboxRoot!);
        var expectedParentName = environment.IsDevelopment ? "development" : "module-test";
        var expectedRoot = Path.GetFullPath(Path.Combine(
            environment.RepositoryRoot!,
            ".qingtoolbox",
            expectedParentName,
            profileName));
        Require(SamePath(sandboxRoot, expectedRoot), "Sandbox root is not the requested profile root.");

        foreach (var path in new[]
        {
            paths.UserModulesDirectory,
            paths.ModuleDataDirectory,
            paths.CacheDirectory,
            paths.QmodStagingDirectory,
            paths.ModuleTransactionsDirectory,
            paths.TempDirectory
        })
        {
            Require(IsWithin(sandboxRoot, path), $"Canary path escaped its sandbox: {path}");
        }

        Require(
            !SamePath(paths.UserModulesDirectory, paths.ModuleDataDirectory) &&
            !SamePath(paths.UserModulesDirectory, paths.CacheDirectory) &&
            !SamePath(paths.ModuleDataDirectory, paths.CacheDirectory),
            "UserModules, Data, and Cache roots must be distinct.");
        Require(
            IsWithin(paths.CacheDirectory, paths.QmodStagingDirectory) &&
            IsWithin(paths.CacheDirectory, paths.ModuleTransactionsDirectory) &&
            !SamePath(paths.QmodStagingDirectory, paths.ModuleTransactionsDirectory),
            "Staging and Transactions must be distinct children of the profile Cache root.");
    }

    private static void AssertInstalledVersion(ApplicationPaths paths, string expectedVersion)
    {
        var installed = Path.Combine(paths.UserModulesDirectory, ModuleId);
        var manifestPath = Path.Combine(installed, "module.json");
        Require(File.Exists(manifestPath), $"Installed manifest is missing: {manifestPath}");
        using var document = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        Require(
            document.RootElement.GetProperty("id").GetString() == ModuleId &&
            document.RootElement.GetProperty("version").GetString() == expectedVersion,
            $"Installed TextTools manifest is not version {expectedVersion}.");
        Require(
            File.Exists(Path.Combine(installed, "QingToolbox.Modules.TextTools.dll")),
            "Installed real TextTools assembly is missing.");
        Require(
            !File.Exists(Path.Combine(installed, QmodPackageStagingService.PackageManifestName)),
            "qmod.json must remain a staging-only manifest.");
    }

    private static void AssertTransactionClean(ApplicationPaths paths)
    {
        var journalFiles = Directory.Exists(paths.ModuleTransactionJournalDirectory)
            ? Directory.EnumerateFiles(
                paths.ModuleTransactionJournalDirectory,
                "*.json",
                SearchOption.AllDirectories).ToArray()
            : [];
        Require(journalFiles.Length == 0, "Completed transaction left a journal behind.");
        Require(
            !Directory.Exists(paths.ModuleTransactionWorkDirectory) ||
            !Directory.EnumerateFileSystemEntries(paths.ModuleTransactionWorkDirectory).Any(),
            "Completed transaction left work or backup content behind.");
    }

    private static void AssertRecoveryArtifacts(ApplicationPaths paths, Guid transactionId)
    {
        var journalFiles = Directory.Exists(paths.ModuleTransactionJournalDirectory)
            ? Directory.EnumerateFiles(
                paths.ModuleTransactionJournalDirectory,
                $"{transactionId:N}.json",
                SearchOption.AllDirectories).ToArray()
            : [];
        Require(journalFiles.Length == 1, "RecoveryRequired transaction journal was not preserved exactly once.");

        var work = Path.Combine(paths.ModuleTransactionWorkDirectory, transactionId.ToString("N"));
        var backup = Path.Combine(work, "backup");
        Require(Directory.Exists(backup), "RecoveryRequired transaction did not preserve the v1 backup.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(backup, "module.json")));
        Require(
            document.RootElement.GetProperty("id").GetString() == ModuleId &&
            document.RootElement.GetProperty("version").GetString() == VersionOne,
            "RecoveryRequired backup is not the original real TextTools v1 payload.");
    }

    private static void DeleteOwnedSandbox(
        ApplicationExecutionEnvironment environment,
        string profileName)
    {
        var root = Path.GetFullPath(environment.SandboxRoot!);
        var environmentDirectory = Path.GetFullPath(Path.Combine(
            environment.RepositoryRoot!,
            ".qingtoolbox",
            environment.IsDevelopment ? "development" : "module-test"));
        Require(
            Directory.GetParent(root)?.FullName is { } parent && SamePath(parent, environmentDirectory) &&
            Path.GetFileName(root).Equals(profileName, StringComparison.Ordinal),
            "Refusing to clean a path that is not the owned canary profile.");
        if (Directory.Exists(root))
        {
            Exception? lastFailure = null;
            for (var attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    Directory.Delete(root, recursive: true);
                    return;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    lastFailure = exception;
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
            throw new IOException($"Could not clean owned canary profile: {root}", lastFailure);
        }
    }

    private static string ScenarioSlug(CanaryScenario scenario) => scenario switch
    {
        CanaryScenario.Success => "success",
        CanaryScenario.Rollback => "rollback",
        CanaryScenario.RecoveryRequired => "recovery",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
    };

    private static string Describe(ModuleUpdateTransactionResult result) =>
        $"succeeded={result.Succeeded}; state={result.State}; failure={result.FailureCode}; " +
        $"rolledBack={result.RolledBack}; cleanupPending={result.CleanupPending}";

    internal static bool IsWithin(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return SamePath(normalizedRoot, normalizedCandidate) ||
               normalizedCandidate.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SamePath(string left, string right) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    internal static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private enum CanaryScenario
    {
        Success,
        Rollback,
        RecoveryRequired
    }

    private sealed record CanaryOptions(
        string RepositoryRoot,
        string VersionOneOutput,
        string VersionTwoOutput,
        string ArtifactsRoot,
        ApplicationEnvironmentKind EnvironmentKind,
        string RunId)
    {
        public static CanaryOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < args.Length; index++)
            {
                var option = args[index];
                if (option is not ("--repository-root" or "--v1-output" or "--v2-output" or
                    "--artifacts-root" or "--environment" or "--run-id" or "--source-commit"))
                {
                    throw new ArgumentException($"Unknown argument: {option}");
                }
                if (++index >= args.Length || !values.TryAdd(option, args[index]))
                {
                    throw new ArgumentException($"A single value is required after {option}.");
                }
            }

            foreach (var required in new[]
            {
                "--repository-root", "--v1-output", "--v2-output", "--artifacts-root",
                "--environment", "--run-id", "--source-commit"
            })
            {
                if (!values.ContainsKey(required))
                {
                    throw new ArgumentException($"Missing required argument: {required}");
                }
            }

            var repositoryRoot = ExistingDirectory(values["--repository-root"], "repository root");
            var versionOneOutput = ExistingDirectory(values["--v1-output"], "v1 output");
            var versionTwoOutput = ExistingDirectory(values["--v2-output"], "v2 output");
            var artifactsRoot = ExistingDirectory(values["--artifacts-root"], "artifact root");
            var tempRoot = Path.GetFullPath(Path.GetTempPath());
            foreach (var path in new[] { versionOneOutput, versionTwoOutput, artifactsRoot })
            {
                Require(IsWithin(tempRoot, path), $"Build/package path must remain in the OS temporary root: {path}");
                Require(!IsWithin(repositoryRoot, path), $"Build/package path must not be inside the repository: {path}");
            }

            if (!Enum.TryParse<ApplicationEnvironmentKind>(
                    values["--environment"],
                    ignoreCase: false,
                    out var kind) ||
                kind is not (ApplicationEnvironmentKind.Development or ApplicationEnvironmentKind.ModuleTest))
            {
                throw new ArgumentException(
                    "--environment must be Development or ModuleTest; Production is forbidden.");
            }

            var runId = values["--run-id"];
            if (runId.Length is < 6 or > 20 || !runId.All(char.IsAsciiLetterOrDigit))
            {
                throw new ArgumentException("--run-id must contain 6-20 ASCII letters or digits.");
            }
            if (!values["--source-commit"].Equals(PinnedSourceCommit, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"TextTools source must be pinned to the full commit {PinnedSourceCommit}.");
            }

            return new(
                repositoryRoot,
                versionOneOutput,
                versionTwoOutput,
                artifactsRoot,
                kind,
                runId);
        }

        private static string ExistingDirectory(string value, string description)
        {
            if (!Path.IsPathFullyQualified(value))
            {
                throw new ArgumentException($"{description} must be an absolute path.");
            }
            var path = Path.GetFullPath(value);
            if (!Directory.Exists(path))
            {
                throw new DirectoryNotFoundException($"{description} does not exist: {path}");
            }
            return path;
        }
    }

    private sealed record PackageArtifact(
        string FilePath,
        string FileName,
        string Version,
        long Size,
        string Sha256)
    {
        public QmodStagingInput CreateStagingInput(string localVersion)
        {
            var verified = new VerifiedModulePackage(
                ModuleId,
                SemanticVersion.Parse(Version),
                FileName,
                FilePath,
                Size,
                Sha256,
                DateTimeOffset.UtcNow);
            var identity = new ModulePackageDownloadIdentity(
                ModuleId,
                localVersion,
                Version,
                FileName,
                $"https://github.com/QingMo-A/QingToolbox/releases/download/texttools-canary/{FileName}",
                Size,
                Sha256);
            return new(
                verified,
                identity,
                ModuleUpdateIdentity.ModuleApiVersion,
                "qingtoolbox-official");
        }
    }

    private static class TextToolsPackageBuilder
    {
        private static readonly UTF8Encoding Utf8NoBom = new(false);

        public static PackageArtifact Create(
            string outputDirectory,
            string packageDirectory,
            string version,
            string fileName)
        {
            var (manifest, relativePayloads) = PreparePayload(outputDirectory, version);

            Directory.CreateDirectory(packageDirectory);
            var packagePath = Path.Combine(packageDirectory, fileName);
            Require(!File.Exists(packagePath), $"Refusing to overwrite canary package: {packagePath}");
            using (var stream = new FileStream(packagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                AddBytes(
                    archive,
                    QmodPackageStagingService.PackageManifestName,
                    Utf8NoBom.GetBytes(
                        $"{{\"schemaVersion\":1,\"moduleId\":\"{ModuleId}\"," +
                        $"\"version\":\"{version}\",\"moduleApiVersion\":" +
                        $"\"{ModuleUpdateIdentity.ModuleApiVersion}\",\"entryManifest\":\"module.json\"}}"));
                AddBytes(
                    archive,
                    "module.json",
                    Utf8NoBom.GetBytes(manifest.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = false
                    })));
                foreach (var relative in relativePayloads.Order(StringComparer.Ordinal))
                {
                    AddFile(archive, outputDirectory, relative);
                }
            }

            var bytes = File.ReadAllBytes(packagePath);
            return new(
                packagePath,
                fileName,
                version,
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        public static void SeedInstalled(
            string outputDirectory,
            string userModulesDirectory,
            string version)
        {
            var (manifest, relativePayloads) = PreparePayload(outputDirectory, version);
            var installed = Path.Combine(userModulesDirectory, ModuleId);
            Require(
                !Directory.Exists(installed) && !File.Exists(installed),
                $"Refusing to replace an existing TextTools seed: {installed}");
            Directory.CreateDirectory(installed);
            File.WriteAllText(
                Path.Combine(installed, "module.json"),
                manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                Utf8NoBom);
            foreach (var relative in relativePayloads.Order(StringComparer.Ordinal))
            {
                CopyPayload(outputDirectory, installed, relative);
            }
        }

        private static (JsonObject Manifest, HashSet<string> RelativePayloads) PreparePayload(
            string outputDirectory,
            string version)
        {
            var sourceManifestPath = Path.Combine(outputDirectory, "module.json");
            Require(File.Exists(sourceManifestPath), $"TextTools output manifest is missing: {sourceManifestPath}");
            var manifest = JsonNode.Parse(File.ReadAllText(sourceManifestPath)) as JsonObject
                ?? throw new InvalidDataException("TextTools module.json must be a JSON object.");
            Require(manifest["id"]?.GetValue<string>() == ModuleId, "TextTools output has the wrong module id.");
            manifest["version"] = version;

            var relativePayloads = new HashSet<string>(StringComparer.Ordinal)
            {
                RequiredString(manifest, "entry")
            };
            if (manifest["icon"] is JsonValue icon)
            {
                relativePayloads.Add(icon.GetValue<string>());
            }
            if (manifest["localization"]?["resources"] is JsonObject resources)
            {
                foreach (var resource in resources)
                {
                    if (resource.Value is JsonValue value)
                    {
                        relativePayloads.Add(value.GetValue<string>());
                    }
                }
            }
            return (manifest, relativePayloads);
        }

        private static string RequiredString(JsonObject value, string propertyName) =>
            value[propertyName]?.GetValue<string>() is { Length: > 0 } result
                ? result
                : throw new InvalidDataException($"TextTools module.json is missing {propertyName}.");

        private static void AddFile(ZipArchive archive, string root, string relativePath)
        {
            var (normalized, source) = ResolvePayload(root, relativePath);

            var entry = archive.CreateEntry(normalized, CompressionLevel.Optimal);
            using var input = File.OpenRead(source);
            using var output = entry.Open();
            input.CopyTo(output);
        }

        private static void CopyPayload(string sourceRoot, string destinationRoot, string relativePath)
        {
            var (normalized, source) = ResolvePayload(sourceRoot, relativePath);
            var destination = Path.GetFullPath(Path.Combine(
                destinationRoot,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            Require(
                IsWithin(destinationRoot, destination),
                $"TextTools payload escaped its installed root: {relativePath}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: false);
        }

        private static (string Normalized, string Source) ResolvePayload(
            string root,
            string relativePath)
        {
            var normalized = relativePath.Replace('\\', '/');
            Require(
                normalized.Length > 0 &&
                !normalized.StartsWith('/') &&
                normalized.Split('/').All(segment => segment is not ("" or "." or "..")),
                $"Unsafe TextTools payload path: {relativePath}");
            var source = Path.GetFullPath(Path.Combine(
                root,
                normalized.Replace('/', Path.DirectorySeparatorChar)));
            Require(IsWithin(root, source), $"TextTools payload escaped its output root: {relativePath}");
            Require(File.Exists(source), $"TextTools payload is missing: {source}");
            return (normalized, source);
        }

        private static void AddBytes(ZipArchive archive, string relativePath, byte[] bytes)
        {
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
            using var output = entry.Open();
            output.Write(bytes);
        }
    }

    private sealed class CanaryRuntimeCoordinator : IModuleUpdateRuntimeCoordinator, IAsyncDisposable
    {
        private const string VariantMetadataKey = "QingToolbox.TextToolsCanary.Variant";
        private const string CommitMetadataKey = "QingToolbox.TextToolsCanary.SourceCommit";
        private readonly ApplicationPaths _paths;
        private readonly string _expectedSourceCommit;
        private readonly ModuleRuntimeManager _runtime;
        private readonly ModuleWindowManager _windows;
        private readonly ModuleUpdateRuntimeCoordinator _adapter;
        private readonly ModuleProcessBroker _broker;
        private readonly SessionLogService _sessionLog;
        private bool _failPromotedRestoreOnce;
        private bool _blockPromotedUnload;
        private readonly HashSet<int> _observedProcesses = [];

        public CanaryRuntimeCoordinator(ApplicationPaths paths, string expectedSourceCommit)
        {
            _paths = paths;
            _expectedSourceCommit = expectedSourceCommit;
            var settings = new UserSettingsService(paths.SettingsPath);
            var localization = new LocalizationManager(settings);
            _runtime = new ModuleRuntimeManager(new InProcessModuleLoader(localization));
            _windows = new ModuleWindowManager(localization);
            _sessionLog = new SessionLogService(paths, TimeProvider.System);
            _broker = new ModuleProcessBroker(paths, _sessionLog);
            _adapter = new ModuleUpdateRuntimeCoordinator(
                _runtime,
                _windows,
                settings,
                new ModuleManifestReader(),
                new ModuleManifestValidator(),
                new ModuleStartupFingerprintService(),
                paths,
                localization,
                _sessionLog,
                _broker);
        }

        public void ArmPromotedRestoreFailure(bool blockPromotedUnload)
        {
            Require(!_failPromotedRestoreOnce, "Promoted restore failure is already armed.");
            _failPromotedRestoreOnce = true;
            _blockPromotedUnload = blockPromotedUnload;
        }

        public async Task LoadAndActivateInstalledAsync(
            string moduleId,
            CancellationToken cancellationToken = default)
        {
            var desired = new ModuleUpdateRuntimeState(
                HasWindows: true,
                IsActive: true,
                IsLoaded: true,
                HasStartupAuthorization: false);
            Require(await _adapter.RestorePreviousRuntimeStateAsync(
                    moduleId, desired, cancellationToken),
                "The real runtime adapter could not load the installed TextTools baseline.");
        }

        public async Task AssertRuntimeAsync(
            string moduleId,
            string expectedManifestVersion,
            string expectedVariant,
            bool active,
            bool hasWindows = true,
            CancellationToken cancellationToken = default)
        {
            var process = _broker.GetState(moduleId)
                ?? throw new InvalidOperationException($"ModuleHost session for {moduleId} is missing.");
            var state = await _adapter.GetRuntimeStateAsync(moduleId, cancellationToken);
            Require(
                process.ProcessRunning && process.HandshakeCompleted && process.ModuleLoaded &&
                process.IsActive == active &&
                process.ManifestVersion == expectedManifestVersion &&
                state.HasWindows == hasWindows &&
                process.RuntimeVariant == expectedVariant,
                $"Runtime state mismatch for {moduleId}: " +
                $"version={process.ManifestVersion}; loaded={process.ModuleLoaded}; " +
                $"active={process.IsActive}; windows={state.HasWindows}.");
            Require(_observedProcesses.Add(process.ProcessId!.Value),
                "A retired ModuleHost process identity was unexpectedly reused in one scenario.");
        }

        public Task<ModuleUpdateRuntimeState> GetRuntimeStateAsync(
            string moduleId,
            CancellationToken cancellationToken) =>
            _adapter.GetRuntimeStateAsync(moduleId, cancellationToken);

        public Task<bool> RequestCloseWindowsAsync(
            string moduleId,
            CancellationToken cancellationToken) =>
            _adapter.RequestCloseWindowsAsync(moduleId, cancellationToken);

        public Task<bool> DeactivateAsync(
            string moduleId,
            CancellationToken cancellationToken) =>
            _adapter.DeactivateAsync(moduleId, cancellationToken);

        public Task<bool> UnloadAsync(
            string moduleId,
            CancellationToken cancellationToken)
        {
            if (_blockPromotedUnload && _broker.GetState(moduleId)?.RuntimeVariant == "v2")
            {
                Console.WriteLine("Canary intentionally refused promoted v2 unload.");
                return Task.FromResult(false);
            }

            return _adapter.UnloadAsync(moduleId, cancellationToken);
        }

        public Task<bool> VerifyUnloadedAsync(
            string moduleId,
            CancellationToken cancellationToken) =>
            _adapter.VerifyUnloadedAsync(moduleId, cancellationToken);

        public async Task<bool> RestorePreviousRuntimeStateAsync(
            string moduleId,
            ModuleUpdateRuntimeState previousState,
            CancellationToken cancellationToken)
        {
            var restored = await _adapter.RestorePreviousRuntimeStateAsync(
                moduleId, previousState, cancellationToken);
            if (restored && _failPromotedRestoreOnce &&
                ReadLoadedAssemblyIdentity(_runtime, moduleId)?.Variant == "v2")
            {
                _failPromotedRestoreOnce = false;
                Console.WriteLine(
                    "Canary intentionally reported promoted v2 restore failure after real load/activation.");
                return false;
            }

            return restored;
        }

        public async Task VerifyEarlyExitPublicationAsync(string moduleId)
        {
            await VerifyFailedPublicationAsync(
                moduleId,
                new ModuleProcessBrokerTestHooks
                {
                    ConfigureWorkerStart = (_, start) =>
                    {
                        start.ArgumentList.Add("--test-exit-after-hello");
                        start.ArgumentList.Add("true");
                    }
                },
                "ModuleHost.ExitedBeforePublication");

            await VerifyFailedPublicationAsync(
                moduleId,
                new ModuleProcessBrokerTestHooks
                {
                    AfterSessionPublishedBeforeExitObservation = (_, process) => process.Kill(true)
                },
                "ModuleHost.ExitedDuringRestore");
        }

        private async Task VerifyFailedPublicationAsync(
            string moduleId,
            ModuleProcessBrokerTestHooks hooks,
            string expectedFailureCode)
        {
            var exitCount = 0;
            var exited = new TaskCompletionSource<ModuleProcessExitedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ModuleProcessExitedEventArgs> handler = (_, args) =>
            {
                if (args.ModuleId != moduleId) return;
                Interlocked.Increment(ref exitCount);
                exited.TrySetResult(args);
            };
            _broker.ProcessExited += handler;
            _broker.TestHooks = hooks;
            try
            {
                var restored = await _adapter.RestorePreviousRuntimeStateAsync(
                    moduleId,
                    new(false, false, true, false),
                    CancellationToken.None);
                Require(!restored, "An early-exit ModuleHost restore reported success.");
                var observed = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(exitCount == 1, "An early worker exit was published more than once.");
                Require(observed.FailureCode == expectedFailureCode ||
                        (expectedFailureCode == "ModuleHost.ExitedBeforePublication" &&
                         observed.FailureCode == "ModuleHost.ExitedDuringRestore"),
                    $"Unexpected early-exit failure code: {observed.FailureCode}.");
                Require(_broker.GetState(moduleId) is null && !_broker.HasSession(moduleId),
                    "A dead worker remained published after restore failure.");
            }
            finally
            {
                _broker.TestHooks = null;
                _broker.ProcessExited -= handler;
            }
        }

        public async Task VerifyWindowSuspendRestoreAsync(string moduleId)
        {
            var before = _broker.GetState(moduleId)
                ?? throw new InvalidOperationException("ModuleHost session is missing before suspend.");
            Require(before.HasWindows && before.WindowVisible,
                "The real worker window was not visible before suspend.");
            Require(await _broker.SuspendWindowsAsync(), "Worker window suspend failed.");
            var suspended = _broker.GetState(moduleId)!;
            Require(suspended.HasWindows && !suspended.WindowVisible &&
                    suspended.RuntimeGeneration == before.RuntimeGeneration && suspended.IsActive == before.IsActive,
                "Suspend closed/recreated the window or changed runtime activation.");
            Require(await _broker.RestoreWindowsAsync(), "Worker window restore failed.");
            var restored = _broker.GetState(moduleId)!;
            Require(restored.HasWindows && restored.WindowVisible &&
                    restored.RuntimeGeneration == before.RuntimeGeneration && restored.IsActive == before.IsActive,
                "Restore recreated the worker or failed to restore its visible window.");
        }

        public async Task VerifyUnexpectedExitRecoveryAsync(
            string moduleId, string manifestVersion, string variant)
        {
            var before = _broker.GetState(moduleId)
                ?? throw new InvalidOperationException("ModuleHost session is missing before crash probe.");
            var exited = new TaskCompletionSource<ModuleProcessExitedEventArgs>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ModuleProcessExitedEventArgs>? handler = null;
            handler = (_, args) =>
            {
                if (args.ModuleId == moduleId && args.RuntimeGeneration == before.RuntimeGeneration)
                    exited.TrySetResult(args);
            };
            _broker.ProcessExited += handler;
            try
            {
                using var process = Process.GetProcessById(before.ProcessId!.Value);
                process.Kill(true);
                var observed = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(!observed.Expected && observed.ProcessId == before.ProcessId,
                    "Unexpected worker exit was not classified or identified correctly.");
                Require(_broker.GetState(moduleId) is null && !_broker.HasSession(moduleId),
                    "The crashed worker session was not removed.");
                await LoadAndActivateInstalledAsync(moduleId);
                var restarted = _broker.GetState(moduleId)
                    ?? throw new InvalidOperationException("The module could not restart after a worker crash.");
                Require(restarted.RuntimeGeneration != before.RuntimeGeneration &&
                        restarted.ProcessId != before.ProcessId,
                    "Worker restart reused stale session identity.");
                await AssertRuntimeAsync(moduleId, manifestVersion, variant, active: true);
            }
            finally { _broker.ProcessExited -= handler; }
        }

        public async Task VerifyMultiWorkerBatchAsync(string primaryModuleId, string batchModuleId)
        {
            var desired = new ModuleUpdateRuntimeState(true, true, true, false);
            Require(await _adapter.RestorePreviousRuntimeStateAsync(batchModuleId, desired, CancellationToken.None),
                "The second batch worker could not start.");
            var primary = _broker.GetState(primaryModuleId)!;
            var secondary = _broker.GetState(batchModuleId)!;
            Require(primary.WindowVisible && secondary.WindowVisible,
                "Batch workers did not start with visible windows.");

            var suspendedPeerExit = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<ModuleProcessExitedEventArgs> suspendedPeerHandler = (_, args) =>
            {
                if (args.ModuleId == primaryModuleId && args.RuntimeGeneration == primary.RuntimeGeneration)
                    suspendedPeerExit.TrySetResult();
            };
            _broker.ProcessExited += suspendedPeerHandler;
            _broker.TestHooks = new ModuleProcessBrokerTestHooks
            {
                AfterBatchSnapshot = (command, _) =>
                {
                    if (command == "SuspendWindow")
                        Process.GetProcessById(primary.ProcessId!.Value).Kill(true);
                }
            };
            try
            {
                Require(!await _broker.SuspendWindowsAsync(),
                    "A partial batch suspend reported complete success.");
                Require(_broker.GetState(batchModuleId) is { WindowVisible: false },
                    "The healthy worker did not receive SuspendWindow after its peer failed.");
                await suspendedPeerExit.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                _broker.TestHooks = null;
                _broker.ProcessExited -= suspendedPeerHandler;
            }

            Require(await _adapter.RestorePreviousRuntimeStateAsync(primaryModuleId, desired, CancellationToken.None),
                "The primary worker could not restart after batch suspend failure.");
            Require(await _broker.SuspendWindowsAsync(), "Batch setup suspend failed.");
            primary = _broker.GetState(primaryModuleId)!;
            var restoredPeerExit = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var restoreGeneration = primary.RuntimeGeneration;
            EventHandler<ModuleProcessExitedEventArgs> restoredPeerHandler = (_, args) =>
            {
                if (args.ModuleId == primaryModuleId && args.RuntimeGeneration == restoreGeneration)
                    restoredPeerExit.TrySetResult();
            };
            _broker.ProcessExited += restoredPeerHandler;
            _broker.TestHooks = new ModuleProcessBrokerTestHooks
            {
                AfterBatchSnapshot = (command, _) =>
                {
                    if (command == "RestoreWindow")
                        Process.GetProcessById(primary.ProcessId!.Value).Kill(true);
                }
            };
            try
            {
                Require(!await _broker.RestoreWindowsAsync(),
                    "A partial batch restore reported complete success.");
                Require(_broker.GetState(batchModuleId) is { WindowVisible: true },
                    "The healthy worker did not receive RestoreWindow after its peer failed.");
                await restoredPeerExit.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                _broker.TestHooks = null;
                _broker.ProcessExited -= restoredPeerHandler;
            }

            Require(await _adapter.RestorePreviousRuntimeStateAsync(primaryModuleId, desired, CancellationToken.None),
                "The primary worker could not restart after batch restore failure.");
            Require(await _adapter.UnloadAsync(batchModuleId, CancellationToken.None),
                "The batch probe worker could not be unloaded.");
        }

        public async Task<bool> RestoreRuntimeStateAsync(
            ModuleUpdateRuntimeRestoreRequest request,
            CancellationToken cancellationToken)
        {
            var restored = await _adapter.RestoreRuntimeStateAsync(request, cancellationToken);
            if (!restored) Console.WriteLine($"ModuleHost restore failure: {_broker.LastFailureCode ?? _adapter.LastRestoreFailureCode ?? "identity-rejected"}.");
            if (restored && _failPromotedRestoreOnce &&
                _broker.GetState(request.ModuleId)?.RuntimeVariant == "v2")
            {
                _failPromotedRestoreOnce = false;
                Console.WriteLine(
                    "Canary intentionally reported promoted v2 restore failure after real window creation.");
                return false;
            }
            return restored;
        }

        public async ValueTask DisposeAsync()
        {
            _windows.CloseAllSafely();
            await _broker.DisposeAsync();
            await _runtime.DisposeAsync();
            _sessionLog.Dispose();
        }


        [MethodImpl(MethodImplOptions.NoInlining)]
        private static LoadedAssemblyIdentity? ReadLoadedAssemblyIdentity(
            ModuleRuntimeManager runtime,
            string moduleId)
        {
            var module = runtime.GetRecord(moduleId)?.Handle?.Module;
            if (module is null)
            {
                return null;
            }
            var assembly = module.GetType().Assembly;
            var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .ToDictionary(attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
            return new(
                assembly.GetName().Name,
                module.GetType().FullName,
                metadata.GetValueOrDefault(VariantMetadataKey),
                metadata.GetValueOrDefault(CommitMetadataKey));
        }

        private sealed record LoadedAssemblyIdentity(
            string? AssemblyName,
            string? ModuleType,
            string? Variant,
            string? SourceCommit);
    }

    private sealed class CanaryLocalizationService : ILocalizationService
    {
        public CultureInfo CurrentCulture { get; } = CultureInfo.GetCultureInfo("en-US");
        public string CurrentLanguageCode => CurrentCulture.Name;
        public event EventHandler? CultureChanged;

        public string GetString(string key) => key;

        public string GetString(string key, params object[] args) =>
            string.Format(CultureInfo.InvariantCulture, key, args);

        public string GetModuleString(string moduleId, string key, string? fallback = null) =>
            fallback ?? key;

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
}
