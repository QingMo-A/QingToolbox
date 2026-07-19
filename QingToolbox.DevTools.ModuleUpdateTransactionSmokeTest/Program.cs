using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
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
    await PostRestoreRollbackAsync(root);
    await TreeLeaseTransactionRacesAsync(root);
    await CleanupAndConcurrencyAsync(root);
    await SecurityHardeningAsync(root);
    await LegacySchemaRecoveryAsync(root);
    HandleBoundRenameRaces(root);
    await CrashRecoveryAsync(root);
    Console.WriteLine("Module update transaction smoke test passed: parent-relative rename, tree leases, schema migration, runtime-consistent rollback, five crash windows and isolation.");
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
    Exception? observed = null;
    await using var service = test.Service(coordinator,
        new ModuleUpdateTransactionTestHooks(ExceptionObserved: exception => observed = exception));
    var result = await service.ExecuteAsync(new(test.Attestation));
    Require(result.Succeeded && result.State == ModuleUpdateTransactionState.Committed,
        $"successful update commits ({result.State}/{result.FailureCode}, rollback={result.RolledBack}, error={observed})");
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

    var trustedRoot = await Fixture.CreateAsync(root, "trusted-root", "qing.root-bound", "2.0.0", "1.0.0");
    var rogueRoot = await Fixture.CreateAsync(root, "rogue-root", "qing.root-bound", "2.0.0", "1.0.0");
    var rootCoordinator = new FakeCoordinator(new(true, true, true, false));
    await using (var service = trustedRoot.Service(rootCoordinator))
        Require((await service.ExecuteAsync(new(rogueRoot.Attestation))).FailureCode ==
                ModuleUpdateTransactionFailureCode.VerifiedStagingInvalid && rootCoordinator.TotalCalls == 0,
            "rogue verified root attestation rejected before runtime coordination");

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
    var backupHooks = new ModuleUpdateTransactionTestHooks(DirectoryRename: (source, destination, stage) =>
    {
        if (stage == SecureDirectoryRenameStage.SourceHandleAttested &&
            source.Equals(backup.Installed, StringComparison.OrdinalIgnoreCase)) throw new IOException("backup");
    });
    await using (var service = backup.Service(new FakeCoordinator(new(false, false, false, false)), backupHooks))
        Require((await service.ExecuteAsync(new(backup.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.BackupMoveFailed, "backup move failure structured");
    Require(Fixture.Version(backup.Installed) == "1.0.0", "backup move failure preserves v1");

    var promotion = await Fixture.CreateAsync(root, "promotion-failure", "qing.promotion", "2.0.0", "1.0.0");
    var promotionHooks = new ModuleUpdateTransactionTestHooks(DirectoryRename: (source, destination, stage) =>
    {
        if (stage == SecureDirectoryRenameStage.SourceHandleAttested && Path.GetFileName(source) == "candidate")
            throw new IOException("promotion");
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
            "transaction-owned candidate corruption rolls back by directory identity");
    }
    Require(Fixture.Version(verify.Installed) == "1.0.0", "corrupt transaction candidate restores verified v1");

    var corruptBackup = await Fixture.CreateAsync(root, "corrupt-backup", "qing.corrupt-backup", "2.0.0", "1.0.0");
    var corruptBackupHooks = new ModuleUpdateTransactionTestHooks(
        DirectoryRename: (source, destination, stage) =>
        {
            if (stage == SecureDirectoryRenameStage.AfterHandleRename && Path.GetFileName(destination) == "backup")
                File.WriteAllText(Path.Combine(destination, "payload.dll"), "tampered-old-program");
        },
        InstalledVerificationStarting: () =>
            File.WriteAllText(Path.Combine(corruptBackup.Installed, "payload.dll"), "force-rollback"));
    await using (var service = corruptBackup.Service(
                     new FakeCoordinator(new(false, false, false, false)), corruptBackupHooks))
    {
        var result = await service.ExecuteAsync(new(corruptBackup.Attestation));
        Require(result.FailureCode == ModuleUpdateTransactionFailureCode.RecoveryRequired &&
                !Directory.Exists(corruptBackup.Installed) &&
                Directory.Exists(Path.Combine(Directory.EnumerateDirectories(
                    Path.Combine(corruptBackup.UserModules, ".qing-transactions")).Single(), "backup")),
            "corrupt backup is never restored as trusted old program");
    }

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

static async Task PostRestoreRollbackAsync(string root)
{
    foreach (var scenario in new[] { "verify", "commit" })
    {
        var fixture = await Fixture.CreateAsync(root, "post-restore-" + scenario,
            "qing.post-restore-" + scenario, "2.0.0", "1.0.0");
        var coordinator = new FakeCoordinator(new(true, true, true, false))
        { CurrentVersion = "1.0.0", InstalledVersion = () => Fixture.Version(fixture.Installed) };
        var hooks = scenario == "verify"
            ? new ModuleUpdateTransactionTestHooks(FinalInstalledVerificationStarting: () =>
                File.WriteAllText(Path.Combine(fixture.Installed, "payload.dll"), "late-corruption"))
            : new ModuleUpdateTransactionTestHooks(JournalPersistStarting: state =>
            { if (state == ModuleUpdateTransactionState.Committed) throw new IOException("commit journal"); });
        await using var service = fixture.Service(coordinator, hooks);
        var result = await service.ExecuteAsync(new(fixture.Attestation));
        Require(result.RolledBack && Fixture.Version(fixture.Installed) == "1.0.0" &&
                coordinator.CurrentVersion == "1.0.0", scenario + " restores matching disk/runtime v1");
        var restoreV2 = coordinator.Calls.IndexOf("Restore:2.0.0");
        var unloadV2 = coordinator.Calls.IndexOf("Unload:2.0.0");
        var restoreV1 = coordinator.Calls.LastIndexOf("Restore:1.0.0");
        Require(restoreV2 >= 0 && unloadV2 > restoreV2 && restoreV1 > unloadV2,
            scenario + " quiesces promoted runtime before restoring previous runtime");
    }

    var blocked = await Fixture.CreateAsync(root, "post-restore-blocked",
        "qing.post-restore-blocked", "2.0.0", "1.0.0");
    var blockedCoordinator = new FakeCoordinator(new(true, true, true, false))
    {
        CurrentVersion = "1.0.0", InstalledVersion = () => Fixture.Version(blocked.Installed),
        FailUnloadOnCall = 2
    };
    var blockedHooks = new ModuleUpdateTransactionTestHooks(FinalInstalledVerificationStarting: () =>
        File.WriteAllText(Path.Combine(blocked.Installed, "payload.dll"), "late-corruption"));
    await using (var service = blocked.Service(blockedCoordinator, blockedHooks))
    {
        var result = await service.ExecuteAsync(new(blocked.Attestation));
        Require(result.FailureCode == ModuleUpdateTransactionFailureCode.RecoveryRequired &&
                Fixture.Version(blocked.Installed) == "2.0.0" && blockedCoordinator.CurrentVersion == "2.0.0",
            "failed promoted-runtime unload preserves v2 and backup for recovery");
    }


    var cancelled = await Fixture.CreateAsync(root, "post-restore-cancelled",
        "qing.post-restore-cancelled", "2.0.0", "1.0.0");
    var cancellation = new CancellationTokenSource();
    var cancelledCoordinator = new FakeCoordinator(new(true, true, true, false))
    { CurrentVersion = "1.0.0", InstalledVersion = () => Fixture.Version(cancelled.Installed) };
    var cancelledHooks = new ModuleUpdateTransactionTestHooks(
        FinalInstalledVerificationStarting: cancellation.Cancel);
    await using (var service = cancelled.Service(cancelledCoordinator, cancelledHooks))
    {
        var result = await service.ExecuteAsync(new(cancelled.Attestation), cancellation.Token);
        Require(result.RolledBack && Fixture.Version(cancelled.Installed) == "1.0.0" &&
                cancelledCoordinator.CurrentVersion == "1.0.0",
            "cancellation after promoted runtime restore completes safe rollback");
    }

    foreach (var scenario in new[] { "progress-write", "partial-false", "partial-throw", "quiesced-write" })
    {
        var fixture = await Fixture.CreateAsync(root, "runtime-fault-" + scenario,
            "qing.runtime-fault-" + scenario, "2.0.0", "1.0.0");
        var coordinator = new FakeCoordinator(new(true, true, true, false))
        { CurrentVersion = "1.0.0", InstalledVersion = () => Fixture.Version(fixture.Installed) };
        var injected = false;
        if (scenario == "partial-false") coordinator.PartialRestoreReturnsFalse = true;
        if (scenario == "partial-throw") coordinator.PartialRestoreThrows = true;
        var hooks = new ModuleUpdateTransactionTestHooks(
            FinalInstalledVerificationStarting: scenario == "quiesced-write"
                ? () => File.WriteAllText(Path.Combine(fixture.Installed, "payload.dll"), "force-rollback")
                : null,
            JournalProgressPersistStarting: progress =>
            {
                if (injected) return;
                if ((scenario == "progress-write" && progress.PromotedRuntimeRestored &&
                     !progress.PromotedRuntimeQuiescedForRollback) ||
                    (scenario == "quiesced-write" && progress.PromotedRuntimeQuiescedForRollback &&
                     !progress.PreviousRuntimeRestoreStarted))
                {
                    injected = true;
                    throw new IOException("runtime progress persistence probe");
                }
            });
        await using var service = fixture.Service(coordinator, hooks);
        var result = await service.ExecuteAsync(new(fixture.Attestation));
        Require(result.RolledBack && Fixture.Version(fixture.Installed) == "1.0.0" &&
                coordinator.CurrentVersion == "1.0.0",
            scenario + " restores matching v1 disk and runtime");
        var restoreV2 = coordinator.Calls.IndexOf("Restore:2.0.0");
        var stateV2 = coordinator.Calls.FindIndex(call => call == "GetState:2.0.0");
        var unloadV2 = coordinator.Calls.IndexOf("Unload:2.0.0");
        var restoreV1 = coordinator.Calls.LastIndexOf("Restore:1.0.0");
        Require(restoreV2 >= 0 && stateV2 > restoreV2 && unloadV2 > stateV2 && restoreV1 > unloadV2,
            scenario + " observes and quiesces partial v2 before v1 restore");
    }
}

static async Task TreeLeaseTransactionRacesAsync(string root)
{
    foreach (var stage in new[] { "installed-to-backup", "candidate-to-installed" })
    {
        var suffix = stage == "installed-to-backup" ? "old" : "new";
        var fixture = await Fixture.CreateAsync(root, "tree-" + suffix,
            "qing.tree-" + suffix, "2.0.0", "1.0.0");
        var blocked = 0;
        var hooks = new ModuleUpdateTransactionTestHooks(TreeLeaseAcquired: actual =>
        {
            if (actual != stage) return;
            var path = actual == "installed-to-backup" ? fixture.Installed :
                Directory.EnumerateDirectories(Path.Combine(fixture.UserModules, ".qing-transactions"))
                    .Select(directory => Path.Combine(directory, "candidate")).Single(Directory.Exists);
            foreach (var action in new Action[]
            {
                () => File.WriteAllText(Path.Combine(path, "payload.dll"), "blocked"),
                () => File.Delete(Path.Combine(path, "module.json")),
                () => File.Move(Path.Combine(path, "module.json"), Path.Combine(path, "module.replaced"))
            })
            {
                try { action(); }
                catch (IOException) { blocked++; }
            }
        });
        await using var service = fixture.Service(new FakeCoordinator(new(false, false, false, false)), hooks);
        var result = await service.ExecuteAsync(new(fixture.Attestation));
        Require(result.Succeeded && blocked == 3 && Fixture.Version(fixture.Installed) == "2.0.0",
            stage + " keeps verified files leased until the rename boundary");
    }

    foreach (var stage in new[] { "installed-to-backup", "candidate-to-installed" })
    {
        var fixture = await Fixture.CreateAsync(root, "tree-growth-" + (stage == "installed-to-backup" ? "old" : "new"),
            "qing.tree-growth-" + (stage == "installed-to-backup" ? "old" : "new"), "2.0.0", "1.0.0");
        var injected = false;
        var hooks = new ModuleUpdateTransactionTestHooks(TreeLeaseAcquired: actual =>
        {
            if (actual != stage || injected) return;
            var path = actual == "installed-to-backup" ? fixture.Installed :
                Directory.EnumerateDirectories(Path.Combine(fixture.UserModules, ".qing-transactions"))
                    .Select(directory => Path.Combine(directory, "candidate")).Single(Directory.Exists);
            File.WriteAllText(Path.Combine(path, "post-snapshot-extra.dll"), "untrusted");
            injected = true;
        });
        await using var service = fixture.Service(new FakeCoordinator(new(false, false, false, false)), hooks);
        var result = await service.ExecuteAsync(new(fixture.Attestation));
        Require(injected && result.RolledBack && Fixture.Version(fixture.Installed) == "1.0.0",
            stage + " detects namespace growth after the second pass and does not promote it");
    }

    var rollback = await Fixture.CreateAsync(root, "tree-rollback", "qing.tree-rollback", "2.0.0", "1.0.0");
    var rollbackBlocked = 0;
    var rollbackHooks = new ModuleUpdateTransactionTestHooks(
        FinalInstalledVerificationStarting: () =>
            File.WriteAllText(Path.Combine(rollback.Installed, "payload.dll"), "force rollback"),
        TreeLeaseAcquired: stage =>
        {
            if (stage is not ("installed-to-failed-candidate" or "backup-to-installed")) return;
            var transactionRoot = Directory.EnumerateDirectories(
                Path.Combine(rollback.UserModules, ".qing-transactions")).Single();
            var path = stage == "installed-to-failed-candidate"
                ? rollback.Installed
                : Path.Combine(transactionRoot, "backup");
            foreach (var action in new Action[]
            {
                () => File.WriteAllText(Path.Combine(path, "payload.dll"), "blocked"),
                () => File.Delete(Path.Combine(path, "module.json")),
                () => File.Move(Path.Combine(path, "module.json"), Path.Combine(path, "module.replaced"))
            })
            {
                try { action(); }
                catch (IOException) { rollbackBlocked++; }
            }
        });
    await using (var service = rollback.Service(new FakeCoordinator(new(false, false, false, false)), rollbackHooks))
    {
        var result = await service.ExecuteAsync(new(rollback.Attestation));
        Require(result.RolledBack && rollbackBlocked == 6 && Fixture.Version(rollback.Installed) == "1.0.0",
            "rollback leases both promoted and backup trees through their rename boundaries");
    }

    var renameGap = await Fixture.CreateAsync(root, "tree-rename-gap", "qing.tree-rename-gap",
        "2.0.0", "1.0.0");
    var gapMutation = false;
    var gapHooks = new ModuleUpdateTransactionTestHooks(DirectoryRename: (source, _, stage) =>
    {
        if (stage != SecureDirectoryRenameStage.BeforeHandleRename ||
            !Path.GetFileName(source).Equals("candidate", StringComparison.Ordinal)) return;
        File.WriteAllText(Path.Combine(source, "payload.dll"), "post-seal mutation");
        gapMutation = true;
    });
    await using (var service = renameGap.Service(new FakeCoordinator(new(false, false, false, false)), gapHooks))
    {
        var result = await service.ExecuteAsync(new(renameGap.Attestation));
        Require(gapMutation && result.RolledBack && Fixture.Version(renameGap.Installed) == "1.0.0",
            "mutation in the native descendant-handle rename gap is detected after rename and never committed");
    }
}

static async Task SecurityHardeningAsync(string root)
{
    var longRoot = Path.Combine(root, "long-handles", new string('x', 70), new string('y', 70), new string('z', 70));
    Directory.CreateDirectory(longRoot);
    var longFile = Path.Combine(longRoot, "payload.bin");
    await File.WriteAllTextAsync(longFile, "stable");
    using (var handle = SecureWindowsFileSystem.OpenStableRead(longFile,
               SecureWindowsFileSystem.PhysicalDirectory(longRoot)))
        Require(RandomAccess.GetLength(handle) == 6, "extended-length stable handle opens beyond MAX_PATH");

    var identitySource = Path.Combine(root, "identity-source");
    var identityDestination = Path.Combine(root, "identity-destination");
    Directory.CreateDirectory(identitySource);
    var identityBefore = SecureWindowsFileSystem.DirectoryIdentity(identitySource);
    Directory.Move(identitySource, identityDestination);
    Require(SecureWindowsFileSystem.DirectoryIdentity(identityDestination) == identityBefore,
        "directory identity survives same-volume rename");
    Directory.Delete(identityDestination);
    Directory.CreateDirectory(identityDestination);
    Require(SecureWindowsFileSystem.DirectoryIdentity(identityDestination) != identityBefore,
        "directory replacement changes file identity");

    foreach (var (name, hooks) in new (string, ModuleUpdateTransactionTestHooks)[]
    {
        ("marker-cleanup", new(MarkerDeleteStarting: () => throw new IOException("marker"))),
        ("work-cleanup", new(WorkDeleteStarting: () => throw new IOException("work"))),
        ("journal-cleanup", new(JournalDeleteStarting: () => throw new IOException("journal"))),
        ("commit-callback", new(StatePersisted: state =>
            { if (state == ModuleUpdateTransactionState.Committed) throw new IOException("callback"); })),
        ("cleanup-journal-write", new(MarkerDeleteStarting: () => throw new IOException("marker"),
            JournalPersistStarting: state => { if (state == ModuleUpdateTransactionState.CleanupPending) throw new IOException("journal"); }))
    })
    {
        var fixture = await Fixture.CreateAsync(root, name, "qing." + name, "2.0.0", "1.0.0");
        await using (var service = fixture.Service(new FakeCoordinator(new(false, false, false, false)), hooks))
        {
            var result = await service.ExecuteAsync(new(fixture.Attestation));
            Require(result.Succeeded && result.CleanupPending && Fixture.Version(fixture.Installed) == "2.0.0",
                name + " cannot roll back a persisted commit");
        }
        await using var recovery = fixture.Service(new FakeCoordinator(new(false, false, false, false)));
        Require((await recovery.RecoverAsync()).CleanupCompleted == 1 && Fixture.Version(fixture.Installed) == "2.0.0",
            name + " cleanup is recoverable and preserves v2");
    }

    foreach (var (name, mutate) in new (string, Action<string>)[]
    {
        ("marker-missing", File.Delete),
        ("marker-wrong", path => File.WriteAllText(path, Guid.NewGuid().ToString("D"), new UTF8Encoding(false))),
        ("marker-bom", path => File.WriteAllText(path, Guid.NewGuid().ToString("D"), new UTF8Encoding(true)))
    })
    {
        var fixture = await Fixture.CreateAsync(root, name, "qing." + name, "2.0.0", "1.0.0");
        var hooks = new ModuleUpdateTransactionTestHooks(InstalledVerificationStarting: () =>
            mutate(Path.Combine(fixture.Installed, ".qing-transaction-owner")));
        await using var service = fixture.Service(new FakeCoordinator(new(false, false, false, false)), hooks);
        var result = await service.ExecuteAsync(new(fixture.Attestation));
        Require(result.FailureCode == ModuleUpdateTransactionFailureCode.RecoveryRequired &&
                Fixture.Version(fixture.Installed) == "2.0.0", name + " preserves unknown installed and backup");
    }

    var reserved = await Fixture.CreateAsync(root, "reserved", "qing.reserved", "2.0.0", "1.0.0");
    reserved.Attestation = Fixture.WithModuleId(reserved.Attestation, ".qing-transactions");
    await using (var service = reserved.Service(new FakeCoordinator(new(false, false, false, false))))
        Require((await service.ExecuteAsync(new(reserved.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.ReservedModuleIdentity, "host-reserved module identity rejected");

    foreach (var device in new[] { "con", "nul", "com1" })
    {
        var fixture = await Fixture.CreateAsync(root, "device-" + device, "qing.device-" + device, "2.0.0", "1.0.0");
        fixture.Attestation = Fixture.WithModuleId(fixture.Attestation, device);
        await using var service = fixture.Service(new FakeCoordinator(new(false, false, false, false)));
        Require((await service.ExecuteAsync(new(fixture.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.ReservedModuleIdentity, device + " device identity rejected");
    }

    var isolated = await Fixture.CreateAsync(root, "journal-isolation", "qing.journal-isolation", "2.0.0", "1.0.0");
    await using (var service = isolated.Service(new FakeCoordinator(new(false, false, false, false))))
    {
        var unrelated = Path.Combine(isolated.Journal, new string('a', 64)); Directory.CreateDirectory(unrelated);
        await File.WriteAllTextAsync(Path.Combine(unrelated, "broken.json"), "{");
        Require((await service.ExecuteAsync(new(isolated.Attestation))).Succeeded,
            "corrupt journal namespace does not block another module");
    }

    var overlapRoot = Path.Combine(root, "overlap"); Directory.CreateDirectory(overlapRoot);
    try
    {
        _ = new ModuleUpdateTransactionService("ModuleTest", overlapRoot, Path.Combine(overlapRoot, "cache"),
            "experimental-0.1", new TestAttestor("ModuleTest", overlapRoot),
            new FakeCoordinator(new(false, false, false, false)));
        throw new Exception("overlapping roots accepted");
    }
    catch (ModuleUpdateTransactionConfigurationException exception)
    { Require(exception.FailureCode == ModuleUpdateTransactionConfigurationFailureCode.OverlappingRoots, "root overlap structured"); }

    var linked = await Fixture.CreateAsync(root, "junction-cleanup", "qing.junction-cleanup", "2.0.0", "1.0.0");
    var outside = Path.Combine(linked.Root, "outside"); Directory.CreateDirectory(outside);
    await File.WriteAllTextAsync(Path.Combine(outside, "sentinel.txt"), "keep");
    var linkedCreated = false;
    var linkHooks = new ModuleUpdateTransactionTestHooks(WorkDeleteStarting: () =>
    {
        var transaction = Directory.EnumerateDirectories(Path.Combine(linked.UserModules, ".qing-transactions")).Single();
        linkedCreated = TryCreateJunction(Path.Combine(transaction, "external-link"), outside);
    });
    await using (var service = linked.Service(new FakeCoordinator(new(false, false, false, false)), linkHooks))
        Require((await service.ExecuteAsync(new(linked.Attestation))).Succeeded, "safe cleanup completes with link entry");
    Require(linkedCreated && await File.ReadAllTextAsync(Path.Combine(outside, "sentinel.txt")) == "keep",
        "safe cleanup never follows a directory link");

    var markerLink = await Fixture.CreateAsync(root, "marker-link", "qing.marker-link", "2.0.0", "1.0.0");
    var markerTarget = Path.Combine(markerLink.Root, "marker-target"); await File.WriteAllTextAsync(markerTarget, "external");
    var markerLinkCreated = false;
    var markerLinkHooks = new ModuleUpdateTransactionTestHooks(InstalledVerificationStarting: () =>
    {
        var marker = Path.Combine(markerLink.Installed, ".qing-transaction-owner"); File.Delete(marker);
        try { File.CreateSymbolicLink(marker, markerTarget); markerLinkCreated = true; } catch { }
    });
    await using (var service = markerLink.Service(new FakeCoordinator(new(false, false, false, false)), markerLinkHooks))
    {
        var result = await service.ExecuteAsync(new(markerLink.Attestation));
        if (markerLinkCreated) Require(result.FailureCode == ModuleUpdateTransactionFailureCode.RecoveryRequired,
            "marker symlink prevents ownership proof");
    }
    Require(await File.ReadAllTextAsync(markerTarget) == "external", "marker symlink target unchanged");

    var lockLink = await Fixture.CreateAsync(root, "lock-link", "qing.lock-link", "2.0.0", "1.0.0");
    await using (var service = lockLink.Service(new FakeCoordinator(new(false, false, false, false))))
    {
        var lockPath = service.GetTransactionLockPathForTest(lockLink.ModuleId); var target = Path.Combine(lockLink.Root, "lock-target");
        await File.WriteAllTextAsync(target, "external"); var created = false;
        try { File.CreateSymbolicLink(lockPath, target); created = true; } catch { }
        if (created) Require((await service.ExecuteAsync(new(lockLink.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.IoFailure, "reparse transaction lock rejected");
        Require(await File.ReadAllTextAsync(target) == "external", "lock link target unchanged");
    }

    var lockDirectoryLink = await Fixture.CreateAsync(root, "lock-directory-link", "qing.lock-directory-link", "2.0.0", "1.0.0");
    await using (var service = lockDirectoryLink.Service(new FakeCoordinator(new(false, false, false, false))))
    {
        var locks = Path.GetDirectoryName(service.GetTransactionLockPathForTest(lockDirectoryLink.ModuleId))!;
        var externalLocks = Path.Combine(lockDirectoryLink.Root, "external-locks"); Directory.CreateDirectory(externalLocks);
        var sentinel = Path.Combine(externalLocks, "sentinel.txt"); await File.WriteAllTextAsync(sentinel, "keep");
        Directory.Delete(locks, false);
        Require(TryCreateJunction(locks, externalLocks), "lock directory junction created");
        Require((await service.ExecuteAsync(new(lockDirectoryLink.Attestation))).FailureCode ==
            ModuleUpdateTransactionFailureCode.IoFailure, "reparse lock directory rejected");
        Require(await File.ReadAllTextAsync(sentinel) == "keep", "lock directory target unchanged");
    }

    var liveLock = await Fixture.CreateAsync(root, "live-lock-lease", "qing.live-lock", "2.0.0", "1.0.0");
    var liveLockMoved = false;
    ModuleUpdateTransactionService? liveLockService = null;
    var liveLockHooks = new ModuleUpdateTransactionTestHooks(LockParentLeaseAcquired: () =>
    {
        var locks = Path.GetDirectoryName(liveLockService!.GetTransactionLockPathForTest(liveLock.ModuleId))!;
        try { Directory.Move(locks, Path.Combine(liveLock.Root, "stolen-locks")); liveLockMoved = true; }
        catch (IOException) { }
    });
    await using (liveLockService = liveLock.Service(new FakeCoordinator(new(false, false, false, false)), liveLockHooks))
        Require((await liveLockService.ExecuteAsync(new(liveLock.Attestation))).Succeeded && !liveLockMoved,
            "live LocksRoot lease blocks replacement during lock creation");

    var journalDirectoryLink = await Fixture.CreateAsync(root, "journal-directory-link", "qing.journal-directory-link", "2.0.0", "1.0.0");
    await using (var service = journalDirectoryLink.Service(new FakeCoordinator(new(false, false, false, false))))
    {
        var journalNamespace = service.GetJournalNamespaceForTest(journalDirectoryLink.ModuleId);
        Directory.CreateDirectory(journalNamespace);
        var externalJournal = Path.Combine(journalDirectoryLink.Root, "external-journal");
        Directory.CreateDirectory(externalJournal);
        var sentinel = Path.Combine(externalJournal, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        Directory.Delete(journalNamespace, false);
        Require(TryCreateJunction(journalNamespace, externalJournal), "journal namespace junction created");
        var result = await service.ExecuteAsync(new(journalDirectoryLink.Attestation));
        Require(result.FailureCode == ModuleUpdateTransactionFailureCode.JournalInvalid &&
                await File.ReadAllTextAsync(sentinel) == "keep",
            "journal namespace replacement is rejected without external writes");
    }

    var liveJournal = await Fixture.CreateAsync(root, "live-journal-lease", "qing.live-journal", "2.0.0", "1.0.0");
    var liveJournalMoved = false;
    var liveJournalTempMutationBlocked = 0;
    ModuleUpdateTransactionService? liveJournalService = null;
    var liveJournalHooks = new ModuleUpdateTransactionTestHooks(JournalTempWritten: () =>
    {
        var journalNamespace = liveJournalService!.GetJournalNamespaceForTest(liveJournal.ModuleId);
        var temp = Directory.EnumerateFiles(journalNamespace, "*.tmp-*", SearchOption.TopDirectoryOnly).Single();
        foreach (var action in new Action[]
        {
            () => File.WriteAllText(temp, "replacement"),
            () => File.Delete(temp),
            () => File.Move(temp, temp + ".replaced")
        })
        {
            try { action(); }
            catch (IOException) { liveJournalTempMutationBlocked++; }
        }
        try { Directory.Move(journalNamespace, Path.Combine(liveJournal.Root, "stolen-journal")); liveJournalMoved = true; }
        catch (IOException) { }
    });
    await using (liveJournalService = liveJournal.Service(
                     new FakeCoordinator(new(false, false, false, false)), liveJournalHooks))
        Require((await liveJournalService.ExecuteAsync(new(liveJournal.Attestation))).Succeeded &&
                !liveJournalMoved && liveJournalTempMutationBlocked >= 3,
            "live Journal temp handle and namespace lease block replacement during atomic persistence");

    var precheck = await Fixture.CreateAsync(root, "precheck-reparse", "qing.precheck", "2.0.0", "1.0.0");
    var externalModule = Path.Combine(precheck.Root, "external-module");
    Fixture.WriteModule(externalModule, precheck.ModuleId, "1.0.0", "external");
    var precheckCoordinator = new FakeCoordinator(new(true, true, true, false));
    await using (var service = precheck.Service(precheckCoordinator))
    {
        Directory.Delete(precheck.Installed, true);
        Require(TryCreateJunction(precheck.Installed, externalModule), "installed module junction created");
        var result = await service.ExecuteAsync(new(precheck.Attestation));
        Require(result.FailureCode == ModuleUpdateTransactionFailureCode.InstalledManifestInvalid &&
                precheckCoordinator.TotalCalls == 0,
            "installed tree precheck rejects reparse before lifecycle calls");
    }

    var orphan = await Fixture.CreateAsync(root, "orphan-temp", "qing.orphan-temp", "2.0.0", "1.0.0");
    await using (var service = orphan.Service(new FakeCoordinator(new(false, false, false, false))))
    {
        var journalNamespace = service.GetJournalNamespaceForTest(orphan.ModuleId); Directory.CreateDirectory(journalNamespace);
        var temp = Path.Combine(journalNamespace, Guid.NewGuid().ToString("N") + ".json.tmp-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(temp, "{}");
        Require((await service.RecoverAsync()).RecoveryRequired == 1 && File.Exists(temp),
            "orphan journal temp is reported and preserved");
    }

    var unknown = await Fixture.CreateAsync(root, "unknown-installed", "qing.unknown-installed", "2.0.0", "1.0.0");
    var unknownHooks = new ModuleUpdateTransactionTestHooks(InstalledVerificationStarting: () =>
    {
        Directory.Delete(unknown.Installed, true); Directory.CreateDirectory(unknown.Installed);
        File.WriteAllText(Path.Combine(unknown.Installed, "unknown.txt"), "preserve");
    });
    await using (var service = unknown.Service(new FakeCoordinator(new(false, false, false, false)), unknownHooks))
    {
        var result = await service.ExecuteAsync(new(unknown.Attestation));
        Require(result.FailureCode == ModuleUpdateTransactionFailureCode.RecoveryRequired &&
                await File.ReadAllTextAsync(Path.Combine(unknown.Installed, "unknown.txt")) == "preserve",
            "unknown installed replacement is neither moved nor deleted");
    }

    foreach (var mutation in new[] { "filename", "hash", "time" })
    {
        var fixture = await Fixture.CreateAsync(root, "journal-" + mutation, "qing.journal-" + mutation,
            "2.0.0", "1.0.0");
        Require(await RunCrashWorkerAsync(fixture, ModuleUpdateTransactionCrashPoint.BackupMovedBeforeJournal) != 0,
            mutation + " journal fixture crashed");
        await using var service = fixture.Service(new FakeCoordinator(new(false, false, false, false)));
        var journal = Directory.EnumerateFiles(service.GetJournalNamespaceForTest(fixture.ModuleId), "*.json").Single();
        if (mutation == "filename") File.Move(journal, Path.Combine(Path.GetDirectoryName(journal)!, Guid.NewGuid().ToString("N") + ".json"));
        else
        {
            var node = JsonNode.Parse(await File.ReadAllTextAsync(journal))!.AsObject();
            if (mutation == "hash") node["packageSha256"] = new string('z', 64);
            else node["updatedAtUtc"] = "not-a-date";
            await File.WriteAllTextAsync(journal, node.ToJsonString(), new UTF8Encoding(false));
        }
        var recovery = await service.RecoverAsync();
        Require(recovery.RecoveryRequired == 1 && !Directory.Exists(fixture.Installed),
            mutation + " journal rejected without guessing at backup ownership");
    }

    var nestedCache = Path.Combine(root, "nested-cache"); Directory.CreateDirectory(nestedCache);
    try
    {
        _ = new ModuleUpdateTransactionService("ModuleTest", Path.Combine(nestedCache, "user"), nestedCache,
            "experimental-0.1", new TestAttestor("ModuleTest", nestedCache),
            new FakeCoordinator(new(false, false, false, false)));
        throw new Exception("user root nested in cache accepted");
    }
    catch (ModuleUpdateTransactionConfigurationException exception)
    { Require(exception.FailureCode == ModuleUpdateTransactionConfigurationFailureCode.OverlappingRoots, "reverse root overlap structured"); }
}

static async Task CrashRecoveryAsync(string root)
{
    foreach (var point in Enum.GetValues<ModuleUpdateTransactionCrashPoint>())
    {
        var suffix = ((int)point).ToString();
        var fixture = await Fixture.CreateAsync(root, "crash-" + suffix, "qing.crash-" + suffix,
            "2.0.0", "1.0.0");
        var dataSentinel = Path.Combine(fixture.Root, "data", "keep.txt");
        var cacheSentinel = Path.Combine(fixture.Root, "module-cache", "keep.txt");
        var otherModuleSentinel = Path.Combine(fixture.UserModules, "qing.other", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(dataSentinel)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cacheSentinel)!);
        Directory.CreateDirectory(Path.GetDirectoryName(otherModuleSentinel)!);
        await File.WriteAllTextAsync(dataSentinel, "data");
        await File.WriteAllTextAsync(cacheSentinel, "cache");
        await File.WriteAllTextAsync(otherModuleSentinel, "other");
        var exitCode = await RunCrashWorkerAsync(fixture, point);
        Require(exitCode != 0, $"worker terminated at {point}");
        if (point == ModuleUpdateTransactionCrashPoint.CandidateCopyInProgress)
        {
            var work = Directory.EnumerateDirectories(Path.Combine(fixture.UserModules, ".qing-transactions")).Single();
            var partialCandidate = Path.Combine(work, "candidate");
            Require(Directory.Exists(partialCandidate) &&
                    Directory.EnumerateFiles(partialCandidate, "*", SearchOption.AllDirectories)
                        .Any(path => Path.GetFileName(path) != ".qing-transaction-owner"),
                "candidate copy crash occurs after a payload file is durably written");
        }
        if (point == ModuleUpdateTransactionCrashPoint.CandidateMovedBeforeJournal)
            await File.WriteAllTextAsync(Path.Combine(fixture.Installed, "payload.dll"), "corrupt-after-crash");
        var recoveryCoordinator = new FakeCoordinator(new(false, false, false, false))
        {
            CurrentVersion = point == ModuleUpdateTransactionCrashPoint.RuntimeRestoredBeforeCommit ? "2.0.0" : null,
            InstalledVersion = () => Fixture.Version(fixture.Installed)
        };
        await using var recovery = fixture.Service(recoveryCoordinator);
        var result = await recovery.RecoverAsync();
        var committed = point == ModuleUpdateTransactionCrashPoint.CommittedBeforeCleanup;
        Require((committed ? result.CleanupCompleted : result.Recovered) == 1,
            $"recovery classified {point}");
        Require(Fixture.Version(fixture.Installed) == (committed ? "2.0.0" : "1.0.0"),
            $"recovery selected correct version at {point}");
        Require(await File.ReadAllTextAsync(dataSentinel) == "data" &&
                await File.ReadAllTextAsync(cacheSentinel) == "cache" &&
                await File.ReadAllTextAsync(otherModuleSentinel) == "other",
            $"recovery preserves data cache and unrelated modules at {point}");
        if (point == ModuleUpdateTransactionCrashPoint.CandidateCopyInProgress)
            Require(recoveryCoordinator.TotalCalls == 0 &&
                    !Directory.EnumerateDirectories(Path.Combine(fixture.UserModules, ".qing-transactions")).Any(),
                "mid-copy recovery deletes only the owned partial candidate without loading it");
        if (point == ModuleUpdateTransactionCrashPoint.RuntimeRestoredBeforeCommit)
            Require(recoveryCoordinator.CurrentVersion == "1.0.0" &&
                    recoveryCoordinator.Calls.Contains("Restore:1.0.0"),
                "post-restore crash recovery restores previous runtime against v1 disk");
    }
}

static async Task LegacySchemaRecoveryAsync(string root)
{
    var fixture = await Fixture.CreateAsync(root, "legacy-schema-3", "qing.legacy-schema-3",
        "2.0.0", "1.0.0");
    var migrated = false;
    var hooks = new ModuleUpdateTransactionTestHooks(JournalPersistStarting: state =>
    { if (state == ModuleUpdateTransactionState.Prepared) migrated = true; });
    await using var service = fixture.Service(new FakeCoordinator(new(false, false, false, false)), hooks);
    var transactionId = Guid.NewGuid();
    var work = Path.Combine(fixture.UserModules, ".qing-transactions", transactionId.ToString("N"));
    var candidate = Path.Combine(work, "candidate");
    Directory.CreateDirectory(candidate);
    SecureWindowsFileSystem.WriteOwnerMarker(work, ".qing-transaction-owner", transactionId);
    SecureWindowsFileSystem.WriteOwnerMarker(candidate, ".qing-transaction-owner", transactionId);
    File.WriteAllText(Path.Combine(candidate, "partial.dll"), "partial");
    var installedIdentity = SecureWindowsFileSystem.DirectoryIdentity(fixture.Installed);
    var candidateIdentity = SecureWindowsFileSystem.DirectoryIdentity(candidate);
    var installedFiles = SecureWindowsFileSystem.CaptureStableTreeSnapshot(fixture.Installed).Entries
        .Where(entry => !entry.IsDirectory && !entry.IsReparsePoint)
        .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
        .Select(entry => $"{{\"relativePath\":\"{entry.RelativePath.Replace('\\', '/')}\",\"size\":{entry.Length},\"sha256\":\"{entry.Sha256}\"}}")
        .ToArray();
    var started = DateTimeOffset.UtcNow;
    var namespaceDirectory = service.GetJournalNamespaceForTest(fixture.ModuleId);
    Directory.CreateDirectory(namespaceDirectory);
    var journalPath = Path.Combine(namespaceDirectory, transactionId.ToString("N") + ".json");
    var legacyFixture = $$"""
    {"schemaVersion":3,"transactionId":"{{transactionId:D}}","environmentIdentity":"ModuleTest","moduleId":"{{fixture.ModuleId}}","sourceVersion":"1.0.0","targetVersion":"2.0.0","moduleApiVersion":"experimental-0.1","packageSha256":"{{new string('a', 64)}}","verifiedStagingIdentity":"{{new string('b', 64)}}","userModulesRootIdentity":"{{SecureWindowsFileSystem.HashIdentity(SecureWindowsFileSystem.PhysicalDirectory(fixture.UserModules))}}","state":1,"previousRuntimeState":{"hasWindows":false,"isActive":false,"isLoaded":false,"hasStartupAuthorization":false},"lifecycleProgress":{"windowsClosed":false,"deactivated":false,"unloaded":false,"runtimeRestored":false},"attempt":1,"lastFailureCode":0,"hadInstalledModule":true,"payloadFiles":[{"relativePath":"payload.dll","size":1,"sha256":"{{new string('c', 64)}}"}],"installedFiles":[{{string.Join(',', installedFiles)}}],"installedDirectoryIdentity":{"volumeSerialNumber":{{installedIdentity.VolumeSerialNumber}},"fileId":"{{installedIdentity.FileId}}"},"candidateDirectoryIdentity":{"volumeSerialNumber":{{candidateIdentity.VolumeSerialNumber}},"fileId":"{{candidateIdentity.FileId}}"},"backupDirectoryIdentity":null,"promotedDirectoryIdentity":null,"startedAtUtc":"{{started:O}}","updatedAtUtc":"{{started:O}}"}
    """;
    await File.WriteAllTextAsync(journalPath, legacyFixture, new UTF8Encoding(false));
    var result = await service.RecoverAsync();
    Require(migrated && result.Recovered == 1 && Fixture.Version(fixture.Installed) == "1.0.0" &&
            !Directory.Exists(work) && !File.Exists(journalPath),
        "strict legacy schema 3 fixture migrates atomically to schema 4 before recovery");

    var rollbackFixture = await Fixture.CreateAsync(root, "legacy-rollback", "qing.legacy-rollback",
        "2.0.0", "1.0.0");
    var rollbackId = Guid.NewGuid();
    var rollbackWork = Path.Combine(rollbackFixture.UserModules, ".qing-transactions", rollbackId.ToString("N"));
    var rollbackBackup = Path.Combine(rollbackWork, "backup");
    Directory.CreateDirectory(rollbackWork);
    SecureWindowsFileSystem.WriteOwnerMarker(rollbackWork, ".qing-transaction-owner", rollbackId);
    Directory.Move(rollbackFixture.Installed, rollbackBackup);
    Fixture.WriteModule(rollbackFixture.Installed, rollbackFixture.ModuleId, "2.0.0", "promoted");
    SecureWindowsFileSystem.WriteOwnerMarker(rollbackFixture.Installed, ".qing-transaction-owner", rollbackId);
    var oldIdentity = SecureWindowsFileSystem.DirectoryIdentity(rollbackBackup);
    var promotedIdentity = SecureWindowsFileSystem.DirectoryIdentity(rollbackFixture.Installed);
    var rollbackCoordinator = new FakeCoordinator(new(true, true, true, false))
    {
        CurrentVersion = "2.0.0",
        InstalledVersion = () => Fixture.Version(rollbackFixture.Installed)
    };
    await using var rollbackService = rollbackFixture.Service(rollbackCoordinator);
    var rollbackNamespace = rollbackService.GetJournalNamespaceForTest(rollbackFixture.ModuleId);
    Directory.CreateDirectory(rollbackNamespace);
    var rollbackJournal = Path.Combine(rollbackNamespace, rollbackId.ToString("N") + ".json");
    var rollbackStarted = DateTimeOffset.UtcNow;
    var rollbackLegacy = $$"""
    {"schemaVersion":3,"transactionId":"{{rollbackId:D}}","environmentIdentity":"ModuleTest","moduleId":"{{rollbackFixture.ModuleId}}","sourceVersion":"1.0.0","targetVersion":"2.0.0","moduleApiVersion":"experimental-0.1","packageSha256":"{{new string('a', 64)}}","verifiedStagingIdentity":"{{new string('b', 64)}}","userModulesRootIdentity":"{{SecureWindowsFileSystem.HashIdentity(SecureWindowsFileSystem.PhysicalDirectory(rollbackFixture.UserModules))}}","state":{{(int)ModuleUpdateTransactionState.RollbackStarted}},"previousRuntimeState":{"hasWindows":true,"isActive":true,"isLoaded":true,"hasStartupAuthorization":false},"lifecycleProgress":{"windowsClosed":true,"deactivated":true,"unloaded":true,"runtimeRestored":false},"attempt":1,"lastFailureCode":{{(int)ModuleUpdateTransactionFailureCode.RuntimeRestoreFailed}},"hadInstalledModule":true,"payloadFiles":{{SnapshotFilesJson(rollbackFixture.Installed, omitOwnerMarker: true)}},"installedFiles":{{SnapshotFilesJson(rollbackBackup)}},"installedDirectoryIdentity":{"volumeSerialNumber":{{oldIdentity.VolumeSerialNumber}},"fileId":"{{oldIdentity.FileId}}"},"candidateDirectoryIdentity":{"volumeSerialNumber":{{promotedIdentity.VolumeSerialNumber}},"fileId":"{{promotedIdentity.FileId}}"},"backupDirectoryIdentity":{"volumeSerialNumber":{{oldIdentity.VolumeSerialNumber}},"fileId":"{{oldIdentity.FileId}}"},"promotedDirectoryIdentity":{"volumeSerialNumber":{{promotedIdentity.VolumeSerialNumber}},"fileId":"{{promotedIdentity.FileId}}"},"startedAtUtc":"{{rollbackStarted:O}}","updatedAtUtc":"{{rollbackStarted:O}}"}
    """;
    await File.WriteAllTextAsync(rollbackJournal, rollbackLegacy, new UTF8Encoding(false));
    var rollbackRecovery = await rollbackService.RecoverAsync();
    var getV2 = rollbackCoordinator.Calls.IndexOf("GetState:2.0.0");
    var unloadV2 = rollbackCoordinator.Calls.IndexOf("Unload:2.0.0");
    var restoreV1 = rollbackCoordinator.Calls.IndexOf("Restore:1.0.0");
    Require(rollbackRecovery.Recovered == 1 && Fixture.Version(rollbackFixture.Installed) == "1.0.0" &&
            getV2 >= 0 && unloadV2 > getV2 && restoreV1 > unloadV2,
        "legacy RollbackStarted conservatively observes and unloads a possibly running v2 before restoring v1");

    var completedFixture = await Fixture.CreateAsync(root, "previous-complete", "qing.previous-complete",
        "2.0.0", "1.0.0");
    var completedId = Guid.NewGuid();
    var completedWork = Path.Combine(completedFixture.UserModules, ".qing-transactions", completedId.ToString("N"));
    var completedFailed = Path.Combine(completedWork, "failed-candidate");
    Directory.CreateDirectory(completedWork);
    SecureWindowsFileSystem.WriteOwnerMarker(completedWork, ".qing-transaction-owner", completedId);
    Fixture.WriteModule(completedFailed, completedFixture.ModuleId, "2.0.0", "isolated");
    SecureWindowsFileSystem.WriteOwnerMarker(completedFailed, ".qing-transaction-owner", completedId);
    var completedOldIdentity = SecureWindowsFileSystem.DirectoryIdentity(completedFixture.Installed);
    var completedFailedIdentity = SecureWindowsFileSystem.DirectoryIdentity(completedFailed);
    var completedCoordinator = new FakeCoordinator(new(true, true, true, false))
    {
        CurrentVersion = "1.0.0",
        InstalledVersion = () => Fixture.Version(completedFixture.Installed)
    };
    await using var completedService = completedFixture.Service(completedCoordinator);
    var completedNamespace = completedService.GetJournalNamespaceForTest(completedFixture.ModuleId);
    Directory.CreateDirectory(completedNamespace);
    var completedJournal = Path.Combine(completedNamespace, completedId.ToString("N") + ".json");
    var completedAt = DateTimeOffset.UtcNow;
    var completedSchema4 = $$"""
    {"schemaVersion":4,"transactionId":"{{completedId:D}}","environmentIdentity":"ModuleTest","moduleId":"{{completedFixture.ModuleId}}","sourceVersion":"1.0.0","targetVersion":"2.0.0","moduleApiVersion":"experimental-0.1","packageSha256":"{{new string('a', 64)}}","verifiedStagingIdentity":"{{new string('b', 64)}}","userModulesRootIdentity":"{{SecureWindowsFileSystem.HashIdentity(SecureWindowsFileSystem.PhysicalDirectory(completedFixture.UserModules))}}","state":{{(int)ModuleUpdateTransactionState.RollbackStarted}},"previousRuntimeState":{"hasWindows":true,"isActive":true,"isLoaded":true,"hasStartupAuthorization":false},"lifecycleProgress":{"windowsClosed":true,"deactivated":true,"unloaded":true,"promotedRuntimeRestoreStarted":true,"promotedRuntimeRestored":true,"promotedRuntimeQuiescedForRollback":true,"previousRuntimeRestoreStarted":true,"previousRuntimeRestoredAfterRollback":true},"attempt":1,"lastFailureCode":{{(int)ModuleUpdateTransactionFailureCode.RuntimeRestoreFailed}},"hadInstalledModule":true,"payloadFiles":{{SnapshotFilesJson(completedFailed, omitOwnerMarker: true)}},"installedFiles":{{SnapshotFilesJson(completedFixture.Installed)}},"installedDirectoryIdentity":{"volumeSerialNumber":{{completedOldIdentity.VolumeSerialNumber}},"fileId":"{{completedOldIdentity.FileId}}"},"candidateDirectoryIdentity":{"volumeSerialNumber":{{completedFailedIdentity.VolumeSerialNumber}},"fileId":"{{completedFailedIdentity.FileId}}"},"backupDirectoryIdentity":null,"promotedDirectoryIdentity":{"volumeSerialNumber":{{completedFailedIdentity.VolumeSerialNumber}},"fileId":"{{completedFailedIdentity.FileId}}"},"startedAtUtc":"{{completedAt:O}}","updatedAtUtc":"{{completedAt:O}}"}
    """;
    await File.WriteAllTextAsync(completedJournal, completedSchema4, new UTF8Encoding(false));
    var completedRecovery = await completedService.RecoverAsync();
    Require(completedRecovery.Recovered == 1 && completedCoordinator.TotalCalls == 0 &&
            Fixture.Version(completedFixture.Installed) == "1.0.0" && !Directory.Exists(completedWork),
        "completed previous-runtime restoration is not mistaken for a promoted v2 during recovery");
}

static string SnapshotFilesJson(string directory, bool omitOwnerMarker = false)
{
    var files = SecureWindowsFileSystem.CaptureStableTreeSnapshot(directory).Entries
        .Where(entry => !entry.IsDirectory && !entry.IsReparsePoint &&
                        (!omitOwnerMarker || entry.RelativePath != ".qing-transaction-owner"))
        .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
        .Select(entry => $"{{\"relativePath\":\"{entry.RelativePath.Replace('\\', '/')}\",\"size\":{entry.Length},\"sha256\":\"{entry.Sha256}\"}}")
        .ToArray();
    return "[" + string.Join(',', files) + "]";
}

static void HandleBoundRenameRaces(string root)
{
    var layout = SecureWindowsFileSystem.RenameLayoutForTest();
    var fixedLayouts = SecureWindowsFileSystem.FixedArchitectureRenameLayoutsForTest();
    Require(IntPtr.Size == 8
            ? layout == (0, 8, 16, 20)
            : layout == (0, 4, 8, 12), "FILE_RENAME_INFORMATION ABI layout is explicit");
    Require(fixedLayouts.X86 == (0, 4, 8, 12) && fixedLayouts.X64 == (0, 8, 16, 20),
        "FILE_RENAME_INFORMATION x86 and x64 offsets are both explicit");
    foreach (var operation in new[] { "installed-backup", "candidate-installed", "installed-failed", "backup-installed" })
    {
        var boundary = Path.Combine(root, "rename-" + operation);
        var source = Path.Combine(boundary, "source");
        var destinationParent = Path.Combine(boundary, "destination-parent");
        var destination = Path.Combine(destinationParent, "destination");
        var displaced = Path.Combine(boundary, "displaced");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destinationParent);
        File.WriteAllText(Path.Combine(source, "sentinel.txt"), "owned");
        var sourceIdentity = SecureWindowsFileSystem.DirectoryIdentity(source);
        var parentIdentity = SecureWindowsFileSystem.DirectoryIdentity(destinationParent);
        var externalReplacementSucceeded = false;
        SecureWindowsFileSystem.RenameDirectoryByIdentity(source, sourceIdentity, destination,
            SecureWindowsFileSystem.PhysicalDirectory(boundary), parentIdentity, testHook: stage =>
            {
                if (stage != SecureDirectoryRenameStage.SourceHandleAttested) return;
                try
                {
                    Directory.Move(source, displaced);
                    Directory.CreateDirectory(source);
                    File.WriteAllText(Path.Combine(source, "unknown.txt"), "unknown");
                    externalReplacementSucceeded = true;
                }
                catch (IOException) { }
            });
        Require(!externalReplacementSucceeded &&
                SecureWindowsFileSystem.DirectoryIdentity(destination) == sourceIdentity &&
                File.ReadAllText(Path.Combine(destination, "sentinel.txt")) == "owned",
            operation + " handle lease prevents path substitution");

        var blockedSource = Path.Combine(boundary, "blocked-source");
        var unknownDestination = Path.Combine(destinationParent, "unknown-destination");
        Directory.CreateDirectory(blockedSource);
        Directory.CreateDirectory(unknownDestination);
        File.WriteAllText(Path.Combine(unknownDestination, "sentinel.txt"), "keep");
        try
        {
            SecureWindowsFileSystem.RenameDirectoryByIdentity(blockedSource,
                SecureWindowsFileSystem.DirectoryIdentity(blockedSource), unknownDestination,
                SecureWindowsFileSystem.PhysicalDirectory(boundary), parentIdentity);
            throw new Exception("unknown destination was overwritten");
        }
        catch (IOException) { }
        Require(File.ReadAllText(Path.Combine(unknownDestination, "sentinel.txt")) == "keep",
            operation + " never overwrites unknown destination");
    }

    var parentBoundary = Path.Combine(root, "rename-destination-parent");
    var parentSource = Path.Combine(parentBoundary, "source");
    var parent = Path.Combine(parentBoundary, "parent");
    var stolenParent = Path.Combine(parentBoundary, "stolen-parent");
    Directory.CreateDirectory(parentSource);
    Directory.CreateDirectory(parent);
    File.WriteAllText(Path.Combine(parentSource, "owned.txt"), "owned");
    var parentReplacementSucceeded = false;
    SecureWindowsFileSystem.RenameDirectoryByIdentity(parentSource,
        SecureWindowsFileSystem.DirectoryIdentity(parentSource), Path.Combine(parent, "destination"),
        SecureWindowsFileSystem.PhysicalDirectory(parentBoundary),
        SecureWindowsFileSystem.DirectoryIdentity(parent), testHook: stage =>
        {
            if (stage != SecureDirectoryRenameStage.DestinationParentHandleAttested) return;
            try
            {
                Directory.Move(parent, stolenParent);
                Directory.CreateDirectory(parent);
                File.WriteAllText(Path.Combine(parent, "external.txt"), "keep");
                parentReplacementSucceeded = true;
            }
            catch (IOException) { }
        });
    Require(!parentReplacementSucceeded &&
            File.ReadAllText(Path.Combine(parent, "destination", "owned.txt")) == "owned",
        "destination parent handle blocks replacement before relative rename");

    foreach (var boundaryName in new[] { "user-modules", "work-root", "journal-root" })
    {
        var boundaryRoot = Path.Combine(root, "ancestor-" + boundaryName);
        var source = Path.Combine(boundaryRoot, "source");
        var ancestor = Path.Combine(boundaryRoot, "ancestor");
        var destinationParent = Path.Combine(ancestor, "parent");
        var movedAncestor = Path.Combine(boundaryRoot, "moved-ancestor");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destinationParent);
        File.WriteAllText(Path.Combine(source, "owned.txt"), boundaryName);
        var ancestorMoved = false;
        SecureWindowsFileSystem.RenameDirectoryByIdentity(source,
            SecureWindowsFileSystem.DirectoryIdentity(source), Path.Combine(destinationParent, "destination"),
            SecureWindowsFileSystem.PhysicalDirectory(boundaryRoot),
            SecureWindowsFileSystem.DirectoryIdentity(destinationParent), testHook: stage =>
            {
                if (stage != SecureDirectoryRenameStage.DestinationParentHandleAttested) return;
                try
                {
                    Directory.Move(ancestor, movedAncestor);
                    Directory.CreateDirectory(destinationParent);
                    File.WriteAllText(Path.Combine(destinationParent, "external.txt"), "keep");
                    ancestorMoved = true;
                }
                catch (IOException) { }
            });
        if (ancestorMoved)
        {
            Require(File.ReadAllText(Path.Combine(movedAncestor, "parent", "destination", "owned.txt")) == boundaryName &&
                    File.ReadAllText(Path.Combine(destinationParent, "external.txt")) == "keep" &&
                    !Directory.Exists(Path.Combine(destinationParent, "destination")),
                boundaryName + " ancestor replacement cannot redirect a parent-relative rename");
        }
        else
        {
            Require(File.ReadAllText(Path.Combine(destinationParent, "destination", "owned.txt")) == boundaryName,
                boundaryName + " ancestor replacement is blocked while the parent lease is held");
        }
    }

    foreach (var invalidLeaf in new[] { "..", "CON", "bad:name", "bad ", "bad.", "e\u0301" })
    {
        try { _ = SecureWindowsFileSystem.ValidateSafeLeafName(invalidLeaf); throw new Exception("unsafe leaf accepted"); }
        catch (IOException) { }
    }


    var changingTree = Path.Combine(root, "changing-tree");
    Directory.CreateDirectory(Path.Combine(changingTree, "child"));
    File.WriteAllText(Path.Combine(changingTree, "child", "payload.txt"), "one");
    try
    {
        _ = SecureWindowsFileSystem.CaptureStableTreeSnapshot(changingTree,
            () => File.WriteAllText(Path.Combine(changingTree, "extra.txt"), "extra"));
        throw new Exception("tree growth was accepted");
    }
    catch (IOException) { }

    File.Delete(Path.Combine(changingTree, "extra.txt"));
    try
    {
        _ = SecureWindowsFileSystem.CaptureStableTreeSnapshot(changingTree, () =>
        {
            Directory.Delete(Path.Combine(changingTree, "child"), true);
            Directory.CreateDirectory(Path.Combine(changingTree, "child"));
            File.WriteAllText(Path.Combine(changingTree, "child", "payload.txt"), "one");
        });
        throw new Exception("tree directory replacement was accepted");
    }
    catch (IOException) { }

    var heldTree = Path.Combine(root, "held-tree");
    Directory.CreateDirectory(Path.Combine(heldTree, "child"));
    File.WriteAllText(Path.Combine(heldTree, "module.json"), "{}");
    File.WriteAllText(Path.Combine(heldTree, "payload.dll"), "payload");
    File.WriteAllText(Path.Combine(heldTree, "child", "nested.txt"), "nested");
    using (var lease = SecureWindowsFileSystem.AcquireStableTreeLease(heldTree))
    {
        var blocked = 0;
        foreach (var action in new Action[]
        {
            () => File.WriteAllText(Path.Combine(heldTree, "payload.dll"), "changed"),
            () => File.Delete(Path.Combine(heldTree, "module.json")),
            () => Directory.Move(Path.Combine(heldTree, "child"), Path.Combine(heldTree, "moved-child"))
        })
        {
            try { action(); } catch (IOException) { blocked++; }
        }
        Require(blocked == 3, "tree lease blocks file and directory mutation");
    }
    File.WriteAllText(Path.Combine(heldTree, "payload.dll"), "released");
    Directory.Delete(heldTree, true);

    var afterPassTree = Path.Combine(root, "after-pass-tree");
    Directory.CreateDirectory(afterPassTree);
    File.WriteAllText(Path.Combine(afterPassTree, "module.json"), "{}");
    try
    {
        using var lease = SecureWindowsFileSystem.AcquireStableTreeLease(afterPassTree,
            afterSecondPass: () => File.WriteAllText(Path.Combine(afterPassTree, "extra.dll"), "extra"));
        throw new Exception("post-second-pass tree growth accepted");
    }
    catch (IOException) { }
    File.Delete(Path.Combine(afterPassTree, "extra.dll"));
    File.WriteAllText(Path.Combine(afterPassTree, "module.json"), "released");
    Directory.Delete(afterPassTree, true);

    var limitedTree = Path.Combine(root, "limited-tree");
    Directory.CreateDirectory(limitedTree);
    File.WriteAllText(Path.Combine(limitedTree, "module.json"), "{}");
    File.WriteAllText(Path.Combine(limitedTree, "two.dll"), "two");
    try
    {
        using var lease = SecureWindowsFileSystem.AcquireStableTreeLease(limitedTree,
            limits: new SecureTreeLeaseLimits(MaximumFiles: 1));
        throw new Exception("tree handle limit accepted");
    }
    catch (IOException) { }
    File.WriteAllText(Path.Combine(limitedTree, "two.dll"), "released");
    Directory.Delete(limitedTree, true);

    var limitedDirectories = Path.Combine(root, "limited-directories");
    Directory.CreateDirectory(Path.Combine(limitedDirectories, "one", "two"));
    File.WriteAllText(Path.Combine(limitedDirectories, "one", "two", "payload.dll"), "payload");
    try
    {
        using var lease = SecureWindowsFileSystem.AcquireStableTreeLease(limitedDirectories,
            limits: new SecureTreeLeaseLimits(MaximumDirectories: 1));
        throw new Exception("tree directory handle limit accepted");
    }
    catch (IOException) { }
    Directory.Move(Path.Combine(limitedDirectories, "one"), Path.Combine(limitedDirectories, "released"));
    Directory.Delete(limitedDirectories, true);
}

static async Task<int> RunCrashWorkerAsync(Fixture fixture, ModuleUpdateTransactionCrashPoint point)
{
    var executable = Environment.ProcessPath!; var start = new ProcessStartInfo(executable)
    { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(Fixture).Assembly.Location);
    foreach (var value in new[] { "--worker-crash", fixture.Root, fixture.PackagePath, fixture.ModuleId, point.ToString() })
        start.ArgumentList.Add(value);
    using var worker = Process.Start(start) ?? throw new Exception("worker unavailable");
    var output = worker.StandardOutput.ReadToEndAsync(); var error = worker.StandardError.ReadToEndAsync();
    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); _ = await output; _ = await error;
    return worker.ExitCode;
}

static bool TryCreateJunction(string link, string target)
{
    try
    {
        var start = new ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = Process.Start(start); if (process is null) return false;
        process.WaitForExit(); return process.ExitCode == 0 && Directory.Exists(link);
    }
    catch { return false; }
}

static async Task WorkerAsync(string[] args)
{
    if (args.Length != 5 || args[0] != "--worker-crash" || !args[1].Contains("QingToolbox-transaction-smoke-") ||
        !Enum.TryParse<ModuleUpdateTransactionCrashPoint>(args[4], out var crashPoint))
        throw new InvalidOperationException("Invalid crash worker arguments.");
    var root = args[1]; var package = args[2]; var moduleId = args[3];
    var fixture = await Fixture.FromExistingAsync(root, package, moduleId, "2.0.0");
    var hooks = new ModuleUpdateTransactionTestHooks(CrashPoint: point =>
    { if (point == crashPoint) Environment.FailFast("transaction crash probe: " + point); });
    await using var service = fixture.Service(new FakeCoordinator(new(false, false, false, false)), hooks);
    await service.ExecuteAsync(new(fixture.Attestation));
    Environment.Exit(91);
}

static void Require(bool condition, string message) { if (!condition) throw new Exception("Assertion failed: " + message); }

sealed class FakeCoordinator(ModuleUpdateRuntimeState state) : IModuleUpdateRuntimeCoordinator
{
    public bool FailClose, FailDeactivate, FailUnload, StillLoaded, FailRestore;
    public bool PartialRestoreReturnsFalse, PartialRestoreThrows;
    public bool Closed, Deactivated, Unloaded, Restored;
    public int TotalCalls;
    public int? FailUnloadOnCall;
    public int UnloadCalls;
    private string? _currentVersion;
    public string? CurrentVersion
    {
        get => _currentVersion;
        set { _currentVersion = value; if (value is not null) _isLoaded = true; }
    }
    public Func<string>? InstalledVersion;
    public List<string> Calls { get; } = [];
    private bool _hasWindows = state.HasWindows;
    private bool _isActive = state.IsActive;
    private bool _isLoaded = state.IsLoaded;
    public Task<ModuleUpdateRuntimeState> GetRuntimeStateAsync(string moduleId, CancellationToken token)
    {
        TotalCalls++;
        Calls.Add("GetState:" + CurrentVersion);
        return Task.FromResult(new ModuleUpdateRuntimeState(_hasWindows, _isActive, _isLoaded,
            state.HasStartupAuthorization));
    }
    public Task<bool> RequestCloseWindowsAsync(string moduleId, CancellationToken token)
    {
        TotalCalls++; Calls.Add("Close:" + CurrentVersion); Closed = !FailClose;
        if (Closed) _hasWindows = false;
        return Task.FromResult(Closed);
    }
    public Task<bool> DeactivateAsync(string moduleId, CancellationToken token)
    {
        TotalCalls++; Calls.Add("Deactivate:" + CurrentVersion); Deactivated = !FailDeactivate;
        if (Deactivated) _isActive = false;
        return Task.FromResult(Deactivated);
    }
    public Task<bool> UnloadAsync(string moduleId, CancellationToken token)
    {
        TotalCalls++; UnloadCalls++; Calls.Add("Unload:" + CurrentVersion);
        var fail = FailUnload || FailUnloadOnCall == UnloadCalls;
        if (!fail) { CurrentVersion = null; _isLoaded = false; _hasWindows = false; _isActive = false; }
        return Task.FromResult(Unloaded = !fail);
    }
    public Task<bool> VerifyUnloadedAsync(string moduleId, CancellationToken token)
    { TotalCalls++; Calls.Add("VerifyUnloaded"); return Task.FromResult(!StillLoaded && !_isLoaded); }
    public Task<bool> RestorePreviousRuntimeStateAsync(string moduleId, ModuleUpdateRuntimeState previous, CancellationToken token)
    {
        TotalCalls++;
        var version = InstalledVersion?.Invoke() ?? "unknown";
        Calls.Add("Restore:" + version);
        if (FailRestore) { FailRestore = false; return Task.FromResult(false); }
        if (PartialRestoreReturnsFalse || PartialRestoreThrows)
        {
            CurrentVersion = version; _hasWindows = previous.HasWindows;
            _isActive = previous.IsActive; _isLoaded = previous.IsLoaded;
            if (PartialRestoreThrows) { PartialRestoreThrows = false; throw new IOException("partial runtime restore"); }
            PartialRestoreReturnsFalse = false;
            return Task.FromResult(false);
        }
        CurrentVersion = version; _hasWindows = previous.HasWindows;
        _isActive = previous.IsActive; _isLoaded = previous.IsLoaded;
        return Task.FromResult(Restored = true);
    }
}

sealed class TestAttestor(string environment, string physicalRoot) : IQmodVerifiedStagingAttestor
{
    public string EnvironmentIdentity => environment;
    public string PhysicalVerifiedRootIdentity => Path.GetFullPath(physicalRoot);
    public Task<QmodVerifiedStagingAttestation?> ReattestAsync(
        QmodVerifiedStagingAttestation attestation, CancellationToken cancellationToken) =>
        Task.FromResult<QmodVerifiedStagingAttestation?>(attestation.EnvironmentIdentity == environment ? attestation : null);
}

sealed class Fixture
{
    private const string Api = "experimental-0.1";
    public required string Root, ModuleId, PackagePath, Installed, Journal, UserModules, CacheRoot;
    public required QmodVerifiedStagingAttestation Attestation;
    public required QmodPackageStagingService Staging;
    public ModuleUpdateTransactionService Service(FakeCoordinator coordinator, ModuleUpdateTransactionTestHooks? hooks = null) =>
        new("ModuleTest", UserModules, CacheRoot, Api, Staging, coordinator, null, hooks);

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
        var staging = new QmodPackageStagingService(Path.Combine(root, "cache", "Staging"),
            TimeProvider.System, "ModuleTest", user);
        var staged = await staging.StageAsync(input);
        if (!staged.Succeeded) throw new Exception("Assertion failed: fixture staged");
        var attestation = await staging.AttestVerifiedStagingAsync(input) ?? throw new Exception("fixture attestation failed");
        return new Fixture { Root = root, ModuleId = moduleId, PackagePath = package,
            Installed = Path.Combine(user, moduleId), Journal = Path.Combine(cache, "Journal"),
            UserModules = user, CacheRoot = cache, Attestation = attestation, Staging = staging };
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
            source.PackageSha256, source.TransactionId, source.EnvironmentIdentity, source.Files,
            source.StagingMetadataFile);
    public static QmodVerifiedStagingAttestation WithModuleId(QmodVerifiedStagingAttestation source, string moduleId) =>
        new(source.Directory, source.PhysicalDirectoryIdentity, source.PhysicalVerifiedRootIdentity,
            source.OfficialReleaseIdentityHash, moduleId, source.TargetVersion, source.ModuleApiVersion,
            source.PackageSha256, source.TransactionId, source.EnvironmentIdentity, source.Files,
            source.StagingMetadataFile);
    private static string Manifest(string id, string version) =>
        $"{{\"id\":\"{id}\",\"name\":\"Probe\",\"description\":\"Probe\",\"version\":\"{version}\",\"entry\":\"payload.dll\",\"runtimeType\":\"InProcess\",\"loadMode\":\"Manual\"}}";
    private static void Add(ZipArchive archive, string name, string text)
    { var entry = archive.CreateEntry(name); using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(text); }
}
