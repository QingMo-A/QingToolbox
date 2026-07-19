using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using QingToolbox.Core.Updates;

if (args.Length > 0)
{
    await WorkerAsync(args);
    return;
}

var root = Path.Combine(Path.GetTempPath(), "QingToolbox-transaction-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(root);
    await SuccessAndIsolationAsync(root);
    await ValidationAndLifecycleAsync(root);
    await FailureRollbackAsync(root);
    await CleanupAndConcurrencyAsync(root);
    await CrashRecoveryAsync(root);
    Console.WriteLine("Module update transaction smoke test passed: atomic promotion, rollback, recovery and isolation.");
}
finally { try { Directory.Delete(root, true); } catch { } }

static async Task SuccessAndIsolationAsync(string root)
{
    var test = await Fixture.CreateAsync(root, "success", "qing.transaction", "2.0.0", installedVersion: "1.0.0");
    var data = Path.Combine(test.Root, "data"); var cache = Path.Combine(test.Root, "module-cache");
    Directory.CreateDirectory(data); Directory.CreateDirectory(cache);
    await File.WriteAllTextAsync(Path.Combine(data, "keep.txt"), "data");
    await File.WriteAllTextAsync(Path.Combine(cache, "keep.txt"), "cache");
    var coordinator = new FakeCoordinator(new(true, true, true, true));
    await using var service = test.Service(coordinator);
    var result = await service.ExecuteAsync(new(test.Attestation));
    Require(result.Succeeded && result.State == ModuleUpdateTransactionState.Committed,
        $"successful update commits ({result.State}/{result.FailureCode}, rollback={result.RolledBack})");
    Require(Fixture.Version(test.Installed) == "2.0.0", "v2 promoted");
    Require(coordinator.Closed && coordinator.Deactivated && coordinator.Unloaded && coordinator.Restored,
        "runtime lifecycle coordinated");
    Require(await File.ReadAllTextAsync(Path.Combine(data, "keep.txt")) == "data" &&
            await File.ReadAllTextAsync(Path.Combine(cache, "keep.txt")) == "cache", "data and cache isolated");
    Require(!Directory.EnumerateFiles(test.Journal, "*.json").Any(), "committed journal cleaned");

    var install = await Fixture.CreateAsync(root, "install", "qing.new-module", "1.0.0", installedVersion: null);
    await using var installService = install.Service(new FakeCoordinator(new(false, false, false, false)));
    var installed = await installService.ExecuteAsync(new(install.Attestation));
    Require(installed.Succeeded && Fixture.Version(install.Installed) == "1.0.0", "new installation commits without backup");
}

static async Task ValidationAndLifecycleAsync(string root)
{
    var same = await Fixture.CreateAsync(root, "same", "qing.same", "1.0.0", "1.0.0");
    await using (var service = same.Service(new FakeCoordinator(new(false, false, false, false))))
        Require((await service.ExecuteAsync(new(same.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.VersionNotNewer, "same version rejected");

    var downgrade = await Fixture.CreateAsync(root, "downgrade", "qing.downgrade", "1.0.0", "2.0.0");
    await using (var service = downgrade.Service(new FakeCoordinator(new(false, false, false, false))))
        Require((await service.ExecuteAsync(new(downgrade.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.VersionNotNewer, "downgrade rejected");

    var identity = await Fixture.CreateAsync(root, "identity", "qing.identity", "2.0.0", "1.0.0");
    Fixture.WriteModule(identity.Installed, "qing.someone-else", "1.0.0", "old");
    await using (var service = identity.Service(new FakeCoordinator(new(false, false, false, false))))
        Require((await service.ExecuteAsync(new(identity.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.ModuleIdentityMismatch, "installed identity mismatch rejected");

    var incompatible = await Fixture.CreateAsync(root, "api", "qing.api", "2.0.0", "1.0.0");
    incompatible.Attestation = Fixture.WithModuleApi(incompatible.Attestation, "future-9");
    await using (var service = incompatible.Service(new FakeCoordinator(new(false, false, false, false))))
        Require((await service.ExecuteAsync(new(incompatible.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.ModuleApiIncompatible, "module API mismatch rejected");

    var tampered = await Fixture.CreateAsync(root, "staging-tamper", "qing.tamper", "2.0.0", "1.0.0");
    await File.WriteAllTextAsync(Path.Combine(tampered.Attestation.Directory, "payload.dll"), "tampered");
    await using (var service = tampered.Service(new FakeCoordinator(new(false, false, false, false))))
        Require((await service.ExecuteAsync(new(tampered.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.VerifiedStagingInvalid, "staging tamper rejected before mutation");

    var corrupt = await Fixture.CreateAsync(root, "corrupt", "qing.corrupt", "2.0.0", "1.0.0");
    await File.WriteAllTextAsync(Path.Combine(corrupt.Installed, "module.json"), "{}");
    await using (var service = corrupt.Service(new FakeCoordinator(new(false, false, false, false))))
        Require((await service.ExecuteAsync(new(corrupt.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.InstalledManifestInvalid, "corrupt installed manifest rejected");

    foreach (var (name, failure, configure) in new (string, ModuleUpdateTransactionFailureCode, Action<FakeCoordinator>)[]
    {
        ("close", ModuleUpdateTransactionFailureCode.RuntimeCloseFailed, item => item.FailClose = true),
        ("deactivate", ModuleUpdateTransactionFailureCode.DeactivateFailed, item => item.FailDeactivate = true),
        ("unload", ModuleUpdateTransactionFailureCode.UnloadFailed, item => item.FailUnload = true),
        ("still-loaded", ModuleUpdateTransactionFailureCode.ModuleStillLoaded, item => item.StillLoaded = true)
    })
    {
        var fixture = await Fixture.CreateAsync(root, "lifecycle-" + name, "qing.life-" + name, "2.0.0", "1.0.0");
        var coordinator = new FakeCoordinator(new(true, true, true, false)); configure(coordinator);
        await using var service = fixture.Service(coordinator);
        var result = await service.ExecuteAsync(new(fixture.Attestation));
        Require(result.FailureCode == failure && Fixture.Version(fixture.Installed) == "1.0.0",
            name + " failure preserves v1");
    }
}

static async Task FailureRollbackAsync(string root)
{
    var copy = await Fixture.CreateAsync(root, "copy-failure", "qing.copy", "2.0.0", "1.0.0");
    var copyHooks = new ModuleUpdateTransactionTestHooks(CandidateCopyStarting: () => throw new IOException());
    await using (var service = copy.Service(new FakeCoordinator(new(false, false, false, false)), copyHooks))
        Require((await service.ExecuteAsync(new(copy.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.CandidateCopyFailed, "candidate copy failure structured");
    Require(Fixture.Version(copy.Installed) == "1.0.0", "copy failure preserves v1");

    var backup = await Fixture.CreateAsync(root, "backup-failure", "qing.backup", "2.0.0", "1.0.0");
    var backupHooks = new ModuleUpdateTransactionTestHooks(DirectoryMove: (source, destination) =>
    {
        if (source.Equals(backup.Installed, StringComparison.OrdinalIgnoreCase)) throw new IOException("backup");
        Directory.Move(source, destination);
    });
    await using (var service = backup.Service(new FakeCoordinator(new(false, false, false, false)), backupHooks))
        Require((await service.ExecuteAsync(new(backup.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.BackupMoveFailed, "backup move failure structured");
    Require(Fixture.Version(backup.Installed) == "1.0.0", "backup move failure preserves v1");

    var promotion = await Fixture.CreateAsync(root, "promotion-failure", "qing.promotion", "2.0.0", "1.0.0");
    var promotionHooks = new ModuleUpdateTransactionTestHooks(DirectoryMove: (source, destination) =>
    {
        if (Path.GetFileName(source) == "candidate") throw new IOException("promotion");
        Directory.Move(source, destination);
    });
    await using (var service = promotion.Service(new FakeCoordinator(new(false, false, false, false)), promotionHooks))
    {
        var result = await service.ExecuteAsync(new(promotion.Attestation));
        Require(result.FailureCode == ModuleUpdateTransactionFailureCode.PromotionFailed && result.RolledBack,
            "promotion failure rolls back");
    }
    Require(Fixture.Version(promotion.Installed) == "1.0.0", "promotion rollback restores v1");

    var verify = await Fixture.CreateAsync(root, "verify-failure", "qing.verify", "2.0.0", "1.0.0");
    var verifyHooks = new ModuleUpdateTransactionTestHooks(InstalledVerificationStarting: () =>
        File.WriteAllText(Path.Combine(verify.Installed, "payload.dll"), "tampered"));
    await using (var service = verify.Service(new FakeCoordinator(new(false, false, false, false)), verifyHooks))
    {
        var result = await service.ExecuteAsync(new(verify.Attestation));
        Require(result.FailureCode == ModuleUpdateTransactionFailureCode.InstalledVerificationFailed && result.RolledBack,
            "installed verification failure rolls back");
    }
    Require(Fixture.Version(verify.Installed) == "1.0.0", "verification rollback restores v1");

    var restore = await Fixture.CreateAsync(root, "restore-failure", "qing.restore", "2.0.0", "1.0.0");
    var restoreCoordinator = new FakeCoordinator(new(false, false, true, false)) { FailRestore = true };
    await using (var service = restore.Service(restoreCoordinator))
    {
        var result = await service.ExecuteAsync(new(restore.Attestation));
        Require(result.FailureCode == ModuleUpdateTransactionFailureCode.RuntimeRestoreFailed && result.RolledBack,
            "runtime restore failure rolls back");
    }
    Require(Fixture.Version(restore.Installed) == "1.0.0", "runtime rollback restores v1");
}

static async Task CleanupAndConcurrencyAsync(string root)
{
    var cleanup = await Fixture.CreateAsync(root, "cleanup", "qing.cleanup", "2.0.0", "1.0.0");
    var cleanupHooks = new ModuleUpdateTransactionTestHooks(BackupCleanupStarting: () => throw new IOException());
    await using (var service = cleanup.Service(new FakeCoordinator(new(false, false, false, false)), cleanupHooks))
    {
        var result = await service.ExecuteAsync(new(cleanup.Attestation));
        Require(result.Succeeded && result.CleanupPending, "backup cleanup failure preserves success");
    }
    await using (var recovery = cleanup.Service(new FakeCoordinator(new(false, false, false, false))))
        Require((await recovery.RecoverAsync()).CleanupCompleted == 1, "cleanup pending recovered");

    var concurrent = await Fixture.CreateAsync(root, "concurrent", "qing.concurrent", "2.0.0", "1.0.0");
    var reached = new ManualResetEventSlim(false); var release = new ManualResetEventSlim(false);
    var hooks = new ModuleUpdateTransactionTestHooks(CandidateCopyStarting: () =>
    { reached.Set(); Require(release.Wait(TimeSpan.FromSeconds(15)), "concurrency holder released"); });
    await using var first = concurrent.Service(new FakeCoordinator(new(false, false, false, false)), hooks);
    await using var second = concurrent.Service(new FakeCoordinator(new(false, false, false, false)));
    var firstTask = Task.Run(() => first.ExecuteAsync(new(concurrent.Attestation)));
    Require(reached.Wait(TimeSpan.FromSeconds(10)), "first transaction owns module lock");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
    var contender = await second.ExecuteAsync(new(concurrent.Attestation), cancellation.Token);
    Require(contender.FailureCode == ModuleUpdateTransactionFailureCode.Cancelled,
        "second transaction waits behind the physical module lock and cancels structurally");
    release.Set(); Require((await firstTask).Succeeded, "first transaction completes after contender cancellation");
}

static async Task CrashRecoveryAsync(string root)
{
    var fixture = await Fixture.CreateAsync(root, "crash", "qing.crash", "2.0.0", "1.0.0");
    var executable = Environment.ProcessPath!; var start = new ProcessStartInfo(executable)
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Fixture).Assembly.Location);
    foreach (var value in new[] { "--worker-crash", fixture.Root, fixture.PackagePath, fixture.ModuleId }) start.ArgumentList.Add(value);
    using var worker = Process.Start(start) ?? throw new Exception("worker unavailable");
    var output = worker.StandardOutput.ReadToEndAsync();
    var error = worker.StandardError.ReadToEndAsync();
    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    _ = await output; _ = await error;
    Require(worker.ExitCode != 0, "worker was terminated at BackupCreated");
    await using var recovery = fixture.Service(new FakeCoordinator(new(false, false, false, false)));
    var result = await recovery.RecoverAsync();
    Require(result.Recovered == 1 && Fixture.Version(fixture.Installed) == "1.0.0",
        "new process recovers BackupCreated journal and v1");
}

static async Task WorkerAsync(string[] args)
{
    if (args.Length != 4 || args[0] != "--worker-crash" || !args[1].Contains("QingToolbox-transaction-smoke-"))
        Environment.Exit(90);
    var root = args[1]; var package = args[2]; var moduleId = args[3];
    var fixture = await Fixture.FromExistingAsync(root, package, moduleId, "2.0.0");
    var hooks = new ModuleUpdateTransactionTestHooks(StatePersisted: state =>
    { if (state == ModuleUpdateTransactionState.BackupCreated) Environment.FailFast("transaction crash probe"); });
    await using var service = fixture.Service(new FakeCoordinator(new(false, false, false, false)), hooks);
    await service.ExecuteAsync(new(fixture.Attestation));
    Environment.Exit(91);
}

static void Require(bool condition, string message) { if (!condition) throw new Exception("Assertion failed: " + message); }

sealed class FakeCoordinator(ModuleUpdateRuntimeState state) : IModuleUpdateRuntimeCoordinator
{
    public bool FailClose, FailDeactivate, FailUnload, StillLoaded, FailRestore;
    public bool Closed, Deactivated, Unloaded, Restored;
    public Task<ModuleUpdateRuntimeState> GetRuntimeStateAsync(string moduleId, CancellationToken token) => Task.FromResult(state);
    public Task<bool> RequestCloseWindowsAsync(string moduleId, CancellationToken token) => Task.FromResult(Closed = !FailClose);
    public Task<bool> DeactivateAsync(string moduleId, CancellationToken token) => Task.FromResult(Deactivated = !FailDeactivate);
    public Task<bool> UnloadAsync(string moduleId, CancellationToken token) => Task.FromResult(Unloaded = !FailUnload);
    public Task<bool> VerifyUnloadedAsync(string moduleId, CancellationToken token) => Task.FromResult(!StillLoaded);
    public Task<bool> RestorePreviousRuntimeStateAsync(string moduleId, ModuleUpdateRuntimeState previous, CancellationToken token)
    {
        if (FailRestore) { FailRestore = false; return Task.FromResult(false); }
        return Task.FromResult(Restored = true);
    }
}

sealed class Fixture
{
    private const string Api = "experimental-0.1";
    public required string Root, ModuleId, PackagePath, Installed, Journal, UserModules, CacheRoot;
    public required QmodVerifiedStagingAttestation Attestation;
    public ModuleUpdateTransactionService Service(FakeCoordinator coordinator, ModuleUpdateTransactionTestHooks? hooks = null) =>
        new("ModuleTest", UserModules, CacheRoot, Api, coordinator, null, hooks);

    public static async Task<Fixture> CreateAsync(string root, string name, string moduleId, string targetVersion,
        string? installedVersion, string moduleApi = Api)
    {
        var fixtureRoot = Path.Combine(root, name); Directory.CreateDirectory(fixtureRoot);
        var package = CreatePackage(fixtureRoot, moduleId, targetVersion, moduleApi);
        var fixture = await FromExistingAsync(fixtureRoot, package, moduleId, targetVersion, moduleApi);
        if (installedVersion is not null) WriteModule(fixture.Installed, moduleId, installedVersion, "old");
        return fixture;
    }

    public static async Task<Fixture> FromExistingAsync(string root, string package, string moduleId, string version,
        string moduleApi = Api)
    {
        var user = Path.Combine(root, "UserModules"); var cache = Path.Combine(root, "cache", "ModuleTransactions");
        Directory.CreateDirectory(user);
        var input = Input(package, moduleId, version, moduleApi);
        await using var staging = new QmodPackageStagingService(Path.Combine(root, "cache", "Staging"),
            TimeProvider.System, "ModuleTest", user);
        var staged = await staging.StageAsync(input);
        if (!staged.Succeeded) throw new Exception("Assertion failed: fixture staged");
        var attestation = await staging.AttestVerifiedStagingAsync(input) ?? throw new Exception("fixture attestation failed");
        return new Fixture { Root = root, ModuleId = moduleId, PackagePath = package,
            Installed = Path.Combine(user, moduleId), Journal = Path.Combine(cache, "Journal"),
            UserModules = user, CacheRoot = cache, Attestation = attestation };
    }

    private static string CreatePackage(string root, string moduleId, string version, string moduleApi)
    {
        var path = Path.Combine(root, "update.qmod");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Add(archive, "qmod.json", $"{{\"schemaVersion\":1,\"moduleId\":\"{moduleId}\",\"version\":\"{version}\",\"moduleApiVersion\":\"{moduleApi}\",\"entryManifest\":\"module.json\"}}");
            Add(archive, "module.json", Manifest(moduleId, version));
            Add(archive, "payload.dll", "new-payload-" + version);
            Add(archive, "i18n/en-US.json", "{}");
        }
        return path;
    }

    private static QmodStagingInput Input(string package, string moduleId, string version, string moduleApi)
    {
        var bytes = File.ReadAllBytes(package); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var file = Path.GetFileName(package); var semantic = SemanticVersion.Parse(version);
        return new(new(moduleId, semantic, file, package, bytes.LongLength, hash, DateTimeOffset.UtcNow),
            new(moduleId, "1.0.0", version, file, $"https://github.com/QingMo-A/QingToolbox/releases/download/v{version}/{file}",
                bytes.LongLength, hash), moduleApi, "qingtoolbox-official");
    }

    public static void WriteModule(string directory, string moduleId, string version, string payload)
    { Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "module.json"), Manifest(moduleId, version)); File.WriteAllText(Path.Combine(directory, "payload.dll"), payload); }
    public static string Version(string directory) => System.Text.Json.JsonDocument.Parse(
        File.ReadAllBytes(Path.Combine(directory, "module.json"))).RootElement.GetProperty("version").GetString()!;
    public static QmodVerifiedStagingAttestation WithModuleApi(QmodVerifiedStagingAttestation source, string moduleApi) =>
        new(source.Directory, source.PhysicalDirectoryIdentity, source.PhysicalVerifiedRootIdentity,
            source.OfficialReleaseIdentityHash, source.ModuleId, source.TargetVersion, moduleApi,
            source.PackageSha256, source.TransactionId, source.EnvironmentIdentity, source.Files);
    private static string Manifest(string id, string version) =>
        $"{{\"id\":\"{id}\",\"name\":\"Probe\",\"description\":\"Probe\",\"version\":\"{version}\",\"entry\":\"payload.dll\",\"runtimeType\":\"InProcess\",\"loadMode\":\"Manual\"}}";
    private static void Add(ZipArchive archive, string name, string text)
    { var entry = archive.CreateEntry(name); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(text); }
}
