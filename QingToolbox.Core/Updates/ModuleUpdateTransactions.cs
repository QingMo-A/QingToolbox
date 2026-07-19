using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace QingToolbox.Core.Updates;

public enum ModuleUpdateTransactionState
{
    Preparing, Prepared, Quiescing, Quiesced, BackupStarted, BackupCreated,
    CandidatePromotionStarted, CandidatePromoted, Verifying, Verified,
    RuntimeRestoring, Committed, CleanupPending, RollbackStarted, RolledBack,
    RecoveryRequired, Failed
}

public enum ModuleUpdateTransactionFailureCode
{
    None, VerifiedStagingInvalid, ModuleNotFound, InstalledManifestInvalid,
    InvalidModuleIdentity, ReservedModuleIdentity, ModuleIdentityMismatch,
    VersionNotNewer, ModuleApiIncompatible, RuntimeCloseFailed, DeactivateFailed,
    UnloadFailed, ModuleStillLoaded, CandidateCopyFailed, CandidateVerificationFailed,
    BackupMoveFailed, PromotionFailed, InstalledVerificationFailed, RuntimeRestoreFailed,
    RollbackFailed, RecoveryRequired, TransactionConflict, JournalInvalid, Cancelled,
    Unauthorized, IoFailure, Unexpected, UnsupportedEnvironment
}

public enum ModuleUpdateTransactionConfigurationFailureCode
{
    UnsupportedEnvironment, InvalidUserModulesRoot, InvalidTransactionCacheRoot,
    ReparseRoot, OverlappingRoots, InvalidModuleApiVersion
}

public sealed class ModuleUpdateTransactionConfigurationException(
    ModuleUpdateTransactionConfigurationFailureCode failureCode, string message) : ArgumentException(message)
{
    public ModuleUpdateTransactionConfigurationFailureCode FailureCode { get; } = failureCode;
}

public sealed record ModuleUpdateRuntimeState(bool HasWindows, bool IsActive, bool IsLoaded, bool HasStartupAuthorization);

public interface IModuleUpdateRuntimeCoordinator
{
    Task<ModuleUpdateRuntimeState> GetRuntimeStateAsync(string moduleId, CancellationToken cancellationToken);
    Task<bool> RequestCloseWindowsAsync(string moduleId, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(string moduleId, CancellationToken cancellationToken);
    Task<bool> UnloadAsync(string moduleId, CancellationToken cancellationToken);
    Task<bool> VerifyUnloadedAsync(string moduleId, CancellationToken cancellationToken);
    Task<bool> RestorePreviousRuntimeStateAsync(string moduleId, ModuleUpdateRuntimeState previousState,
        CancellationToken cancellationToken);
}

public sealed record ModuleUpdateTransactionInput(QmodVerifiedStagingAttestation VerifiedStaging);
public sealed record ModuleUpdateTransactionResult(bool Succeeded, Guid TransactionId,
    ModuleUpdateTransactionState State, ModuleUpdateTransactionFailureCode FailureCode,
    bool RolledBack = false, bool CleanupPending = false);
public sealed record ModuleUpdateRecoveryResult(int Recovered, int CleanupCompleted, int RecoveryRequired,
    IReadOnlyList<ModuleUpdateTransactionResult> Results);
public sealed record ModuleUpdateTransactionLogEvent(string EventName, string ModuleId, string SourceVersion,
    string TargetVersion, string TransactionIdPrefix, ModuleUpdateTransactionState State,
    ModuleUpdateTransactionFailureCode FailureCode = ModuleUpdateTransactionFailureCode.None);

internal enum ModuleUpdateTransactionCrashPoint
{
    CandidateCopyInProgress, BackupMovedBeforeJournal, CandidateMovedBeforeJournal, RuntimeRestoredBeforeCommit,
    CommittedBeforeCleanup
}

internal sealed record ModuleUpdateTransactionTestHooks(
    Action<ModuleUpdateTransactionState>? StatePersisted = null,
    Action<string, string, SecureDirectoryRenameStage>? DirectoryRename = null,
    Action? CandidateCopyStarting = null,
    Action? InstalledVerificationStarting = null,
    Action? FinalInstalledVerificationStarting = null,
    Action? BackupCleanupStarting = null,
    Action<ModuleUpdateTransactionCrashPoint>? CrashPoint = null,
    Action? MarkerDeleteStarting = null,
    Action? WorkDeleteStarting = null,
    Action? JournalDeleteStarting = null,
    Action<ModuleUpdateTransactionState>? JournalPersistStarting = null,
    Action? JournalTempWritten = null,
    Action? LockParentLeaseAcquired = null,
    Action<string>? TreeLeaseAcquired = null,
    Action<ModuleUpdateTransactionService.LifecycleProgress>? JournalProgressPersistStarting = null,
    Action<Exception>? ExceptionObserved = null);

public sealed class ModuleUpdateTransactionService : IAsyncDisposable
{
    private const int JournalSchema = 4;
    private const int LegacyJournalSchema = 3;
    private const string OwnershipMarkerName = ".qing-transaction-owner";
    private readonly string _environmentIdentity;
    private readonly string _userModulesRoot;
    private readonly string _journalRoot;
    private readonly string _locksRoot;
    private readonly string _workRoot;
    private readonly string _physicalUserModulesRoot;
    private readonly string _physicalCacheRoot;
    private readonly SecureDirectoryIdentity _userModulesRootIdentity;
    private readonly string _physicalJournalRoot;
    private readonly string _physicalLocksRoot;
    private readonly SecureDirectoryIdentity _journalRootIdentity;
    private readonly SecureDirectoryIdentity _locksRootIdentity;
    private readonly ConcurrentDictionary<string, SecureDirectoryIdentity> _journalNamespaceIdentities =
        new(StringComparer.Ordinal);
    private readonly string _moduleApiVersion;
    private readonly IQmodVerifiedStagingAttestor _stagingAttestor;
    private readonly IModuleUpdateRuntimeCoordinator _runtime;
    private readonly Action<ModuleUpdateTransactionLogEvent>? _log;
    private readonly ModuleUpdateTransactionTestHooks? _hooks;
    private int _disposed;
    private static readonly JsonSerializerOptions JournalOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ModuleUpdateTransactionService(string environmentIdentity, string userModulesRoot,
        string transactionCacheRoot, string moduleApiVersion, IQmodVerifiedStagingAttestor stagingAttestor,
        IModuleUpdateRuntimeCoordinator runtime,
        Action<ModuleUpdateTransactionLogEvent>? log = null)
        : this(environmentIdentity, userModulesRoot, transactionCacheRoot, moduleApiVersion, stagingAttestor,
            runtime, log, null) { }

    internal ModuleUpdateTransactionService(string environmentIdentity, string userModulesRoot,
        string transactionCacheRoot, string moduleApiVersion, IQmodVerifiedStagingAttestor stagingAttestor,
        IModuleUpdateRuntimeCoordinator runtime,
        Action<ModuleUpdateTransactionLogEvent>? log, ModuleUpdateTransactionTestHooks? hooks)
    {
        if (environmentIdentity is not ("Development" or "ModuleTest"))
            throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.UnsupportedEnvironment);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(stagingAttestor);
        try { _userModulesRoot = SecureWindowsFileSystem.ValidateAbsoluteNonRoot(userModulesRoot); }
        catch { throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.InvalidUserModulesRoot); }
        string cache;
        try { cache = SecureWindowsFileSystem.ValidateAbsoluteNonRoot(transactionCacheRoot); }
        catch { throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.InvalidTransactionCacheRoot); }
        _environmentIdentity = environmentIdentity;
        _journalRoot = Path.Combine(cache, "Journal"); _locksRoot = Path.Combine(cache, "Locks");
        _workRoot = Path.Combine(_userModulesRoot, ".qing-transactions");
        if (string.IsNullOrWhiteSpace(moduleApiVersion))
            throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.InvalidModuleApiVersion);
        _moduleApiVersion = moduleApiVersion; _stagingAttestor = stagingAttestor;
        _runtime = runtime; _log = log; _hooks = hooks;
        try
        {
            foreach (var root in new[] { _userModulesRoot, cache, _journalRoot, _locksRoot, _workRoot })
                SecureWindowsFileSystem.CreateOrdinaryDirectoryTree(root);
            _physicalUserModulesRoot = SecureWindowsFileSystem.PhysicalDirectory(_userModulesRoot);
            _userModulesRootIdentity = SecureWindowsFileSystem.DirectoryIdentity(_userModulesRoot);
            _physicalCacheRoot = SecureWindowsFileSystem.PhysicalDirectory(cache);
            _physicalJournalRoot = SecureWindowsFileSystem.PhysicalDirectory(_journalRoot);
            _physicalLocksRoot = SecureWindowsFileSystem.PhysicalDirectory(_locksRoot);
            _journalRootIdentity = SecureWindowsFileSystem.DirectoryIdentity(_journalRoot);
            _locksRootIdentity = SecureWindowsFileSystem.DirectoryIdentity(_locksRoot);
            if (SecureWindowsFileSystem.IsWithin(_physicalUserModulesRoot, _physicalCacheRoot) ||
                SecureWindowsFileSystem.IsWithin(_physicalCacheRoot, _physicalUserModulesRoot))
                throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.OverlappingRoots);
            if (!Path.GetPathRoot(_physicalUserModulesRoot)!.Equals(
                    Path.GetPathRoot(SecureWindowsFileSystem.PhysicalDirectory(_workRoot)), StringComparison.OrdinalIgnoreCase))
                throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.InvalidUserModulesRoot);
            if (_stagingAttestor.EnvironmentIdentity != _environmentIdentity)
                throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.UnsupportedEnvironment);
        }
        catch (ModuleUpdateTransactionConfigurationException) { throw; }
        catch { throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.ReparseRoot); }
    }

    public async Task<ModuleUpdateTransactionResult> ExecuteAsync(ModuleUpdateTransactionInput input,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(input); var attestation = input.VerifiedStaging;
        var transactionId = Guid.NewGuid();
        var identityFailure = SecureModuleIdentity.Validate(attestation.ModuleId);
        if (identityFailure != ModuleIdentityFailure.None)
            return Failure(transactionId, identityFailure == ModuleIdentityFailure.Reserved
                ? ModuleUpdateTransactionFailureCode.ReservedModuleIdentity
                : ModuleUpdateTransactionFailureCode.InvalidModuleIdentity);
        if (attestation.EnvironmentIdentity != _environmentIdentity) return Failure(transactionId,
            ModuleUpdateTransactionFailureCode.VerifiedStagingInvalid);
        if (attestation.ModuleApiVersion != _moduleApiVersion) return Failure(transactionId,
            ModuleUpdateTransactionFailureCode.ModuleApiIncompatible);

        CrashRecoverableFileLock transactionLock;
        try { transactionLock = await AcquireTransactionLockAsync(attestation.ModuleId, cancellationToken); }
        catch (OperationCanceledException) { return Failure(transactionId, ModuleUpdateTransactionFailureCode.Cancelled); }
        catch (UnauthorizedAccessException) { return Failure(transactionId, ModuleUpdateTransactionFailureCode.Unauthorized); }
        catch (IOException) { return Failure(transactionId, ModuleUpdateTransactionFailureCode.IoFailure); }
        await using var heldLock = transactionLock;
        QmodVerifiedStagingAttestation? trustedAttestation;
        try { trustedAttestation = await _stagingAttestor.ReattestAsync(attestation, cancellationToken); }
        catch (OperationCanceledException) { return Failure(transactionId, ModuleUpdateTransactionFailureCode.Cancelled); }
        catch { trustedAttestation = null; }
        if (trustedAttestation is null || !ReattestVerifiedStaging(trustedAttestation)) return Failure(transactionId,
            ModuleUpdateTransactionFailureCode.VerifiedStagingInvalid);
        attestation = trustedAttestation;
        try
        {
            if (ModuleJournalEntries(attestation.ModuleId).Any()) return Failure(transactionId,
                ModuleUpdateTransactionFailureCode.RecoveryRequired, ModuleUpdateTransactionState.RecoveryRequired);
        }
        catch
        {
            return Failure(transactionId, ModuleUpdateTransactionFailureCode.JournalInvalid,
                ModuleUpdateTransactionState.RecoveryRequired);
        }

        var installed = InstalledPath(attestation.ModuleId); var work = WorkPath(transactionId);
        var candidate = Path.Combine(work, "candidate"); var backup = Path.Combine(work, "backup");
        var failed = Path.Combine(work, "failed-candidate");
        var payload = Payload(attestation); TransactionJournal journal; bool commitPersisted = false;
        var promotedRuntimeRestoreAttempted = false;
        var promotedRuntimeMayBeRunning = false;
        InstalledSnapshot? installedSnapshot = null;
        if (Directory.Exists(installed))
        {
            installedSnapshot = ReadInstalledSnapshot(installed);
            if (installedSnapshot is null) return Failure(transactionId,
                ModuleUpdateTransactionFailureCode.InstalledManifestInvalid);
            if (installedSnapshot.ModuleId != attestation.ModuleId) return Failure(transactionId,
                ModuleUpdateTransactionFailureCode.ModuleIdentityMismatch);
            if (SemanticVersion.Parse(attestation.TargetVersion).CompareTo(
                    SemanticVersion.Parse(installedSnapshot.Version)) <= 0)
                return Failure(transactionId, ModuleUpdateTransactionFailureCode.VersionNotNewer);
        }
        else if (File.Exists(installed)) return Failure(transactionId,
            ModuleUpdateTransactionFailureCode.InstalledManifestInvalid);
        try
        {
            Directory.CreateDirectory(work); SecureWindowsFileSystem.WriteOwnerMarker(work, OwnershipMarkerName, transactionId);
            var previousRuntime = await _runtime.GetRuntimeStateAsync(attestation.ModuleId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            journal = new(transactionId, _environmentIdentity, attestation.ModuleId,
                installedSnapshot?.Version ?? "", attestation.TargetVersion,
                attestation.ModuleApiVersion, attestation.PackageSha256,
                SecureWindowsFileSystem.HashIdentity(attestation.PhysicalDirectoryIdentity),
                SecureWindowsFileSystem.HashIdentity(_physicalUserModulesRoot), ModuleUpdateTransactionState.Preparing,
                previousRuntime, new(false, false, false, false, false, false, false, false), 1,
                ModuleUpdateTransactionFailureCode.None,
                installedSnapshot is not null, payload, installedSnapshot?.Files ?? [],
                installedSnapshot?.DirectoryIdentity, null, null, null, now, now);
            Persist(journal); Log("Module update transaction started", journal); Log("Verified staging attested", journal);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            TryDeleteWork(work, transactionId); return Failure(transactionId, Map(exception));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _hooks?.CandidateCopyStarting?.Invoke(); BuildCandidate(attestation, candidate, transactionId, payload);
            journal = Advance(journal with
            { CandidateDirectoryIdentity = SecureWindowsFileSystem.DirectoryIdentity(candidate) },
                ModuleUpdateTransactionState.Prepared);
            journal = Advance(journal, ModuleUpdateTransactionState.Quiescing);
            if (journal.PreviousRuntimeState.HasWindows)
            {
                if (!await _runtime.RequestCloseWindowsAsync(journal.ModuleId, cancellationToken))
                    return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                        ModuleUpdateTransactionFailureCode.RuntimeCloseFailed, cancellationToken);
                journal = UpdateProgress(journal, journal.LifecycleProgress with { WindowsClosed = true });
            }
            if (journal.PreviousRuntimeState.IsActive)
            {
                if (!await _runtime.DeactivateAsync(journal.ModuleId, cancellationToken))
                    return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                        ModuleUpdateTransactionFailureCode.DeactivateFailed, cancellationToken);
                journal = UpdateProgress(journal, journal.LifecycleProgress with { Deactivated = true });
            }
            if (journal.PreviousRuntimeState.IsLoaded)
            {
                if (!await _runtime.UnloadAsync(journal.ModuleId, cancellationToken))
                    return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                        ModuleUpdateTransactionFailureCode.UnloadFailed, cancellationToken);
                journal = UpdateProgress(journal, journal.LifecycleProgress with { Unloaded = true });
            }
            if (!await _runtime.VerifyUnloadedAsync(journal.ModuleId, cancellationToken))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.ModuleStillLoaded, cancellationToken);
            journal = Advance(journal, ModuleUpdateTransactionState.Quiesced);

            if (journal.HadInstalledModule)
            {
                using var installedLease = AcquireInstalledSnapshotLease(installed, journal);
                if (installedLease is null) return RequireRecovery(journal);
                _hooks?.TreeLeaseAcquired?.Invoke("installed-to-backup");
                installedLease.VerifyAtPath(installed);
                journal = Advance(journal, ModuleUpdateTransactionState.BackupStarted);
                Move(installedLease, installed, backup,
                    SecureWindowsFileSystem.DirectoryIdentity(work));
                _hooks?.CrashPoint?.Invoke(ModuleUpdateTransactionCrashPoint.BackupMovedBeforeJournal);
                var backupIdentity = SecureWindowsFileSystem.DirectoryIdentity(backup);
                if (backupIdentity != journal.InstalledDirectoryIdentity) return RequireRecovery(journal);
                installedLease.VerifyAtPath(backup);
                journal = Advance(journal with { BackupDirectoryIdentity = backupIdentity },
                    ModuleUpdateTransactionState.BackupCreated);
            }
            journal = Advance(journal, ModuleUpdateTransactionState.CandidatePromotionStarted);
            using (var candidateLease = AcquirePayloadTreeLease(candidate, payload, journal.ModuleId,
                       journal.TargetVersion, journal.TransactionId))
            {
                if (candidateLease is null || candidateLease.Snapshot.RootIdentity != journal.CandidateDirectoryIdentity)
                    return RequireRecovery(journal);
                _hooks?.TreeLeaseAcquired?.Invoke("candidate-to-installed");
                candidateLease.VerifyAtPath(candidate);
                Move(candidateLease, candidate, installed, _userModulesRootIdentity);
                candidateLease.VerifyAtPath(installed);
            }
            _hooks?.CrashPoint?.Invoke(ModuleUpdateTransactionCrashPoint.CandidateMovedBeforeJournal);
            var promotedIdentity = SecureWindowsFileSystem.DirectoryIdentity(installed);
            if (promotedIdentity != journal.CandidateDirectoryIdentity) return RequireRecovery(journal);
            journal = Advance(journal with { PromotedDirectoryIdentity = promotedIdentity },
                ModuleUpdateTransactionState.CandidatePromoted);
            journal = Advance(journal, ModuleUpdateTransactionState.Verifying);
            _hooks?.InstalledVerificationStarting?.Invoke();
            if (!ValidateInstalledCandidate(installed, payload, journal.ModuleId, journal.TargetVersion, transactionId))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.InstalledVerificationFailed, cancellationToken);
            journal = Advance(journal, ModuleUpdateTransactionState.Verified);
            journal = Advance(journal, ModuleUpdateTransactionState.RuntimeRestoring);
            journal = UpdateProgress(journal,
                journal.LifecycleProgress with { PromotedRuntimeRestoreStarted = true });
            promotedRuntimeRestoreAttempted = true;
            promotedRuntimeMayBeRunning = true;
            if (!await _runtime.RestorePreviousRuntimeStateAsync(journal.ModuleId, journal.PreviousRuntimeState, cancellationToken))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.RuntimeRestoreFailed, cancellationToken,
                    promotedRuntimeRestoreAttempted, promotedRuntimeMayBeRunning);
            var restoredProgress = journal with
            {
                LifecycleProgress = journal.LifecycleProgress with { PromotedRuntimeRestored = true },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            journal = restoredProgress;
            _hooks?.JournalProgressPersistStarting?.Invoke(journal.LifecycleProgress);
            Persist(journal);
            _hooks?.FinalInstalledVerificationStarting?.Invoke();
            if (!ValidateInstalledCandidate(installed, payload, journal.ModuleId, journal.TargetVersion, transactionId))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.InstalledVerificationFailed, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _hooks?.CrashPoint?.Invoke(ModuleUpdateTransactionCrashPoint.RuntimeRestoredBeforeCommit);
            journal = journal with { State = ModuleUpdateTransactionState.Committed, UpdatedAtUtc = DateTimeOffset.UtcNow };
            Persist(journal); commitPersisted = true;
            _hooks?.StatePersisted?.Invoke(ModuleUpdateTransactionState.Committed);
            Log("Transaction commit persisted", journal);
            _hooks?.CrashPoint?.Invoke(ModuleUpdateTransactionCrashPoint.CommittedBeforeCleanup);
            return PostCommitCleanup(journal, installed, work);
        }
        catch (Exception exception)
        {
            _hooks?.ExceptionObserved?.Invoke(exception);
            if (commitPersisted) return PostCommitPending(journal);
            return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                Map(exception, journal.State), CancellationToken.None,
                promotedRuntimeRestoreAttempted, promotedRuntimeMayBeRunning);
        }
    }

    public async Task<ModuleUpdateRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var results = new List<ModuleUpdateTransactionResult>(); int recovered = 0, cleaned = 0, required = 0;
        try { ValidateJournalRoot(); }
        catch { return new(0, 0, 1, results); }
        foreach (var namespaceDirectory in Directory.EnumerateDirectories(_journalRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SecureWindowsFileSystem.IsReparsePath(namespaceDirectory) ||
                !SecureWindowsFileSystem.IsWithin(_physicalJournalRoot,
                    SecureWindowsFileSystem.PhysicalDirectory(namespaceDirectory))) { required++; continue; }
            foreach (var path in Directory.EnumerateFiles(namespaceDirectory, "*.json", SearchOption.TopDirectoryOnly).ToArray())
            {
                JournalEnvelope envelope;
                try { envelope = ReadJournalEnvelope(path, namespaceDirectory); }
                catch { required++; continue; }
                CrashRecoverableFileLock transactionLock;
                try { transactionLock = await AcquireTransactionLockAsync(envelope.ModuleId, cancellationToken); }
                catch { required++; continue; }
                await using var held = transactionLock;
                var installed = InstalledPath(envelope.ModuleId); var work = WorkPath(envelope.TransactionId);
                var candidate = Path.Combine(work, "candidate"); var backup = Path.Combine(work, "backup");
                var failed = Path.Combine(work, "failed-candidate");
                TransactionJournal journal;
                bool legacy;
                try
                {
                    journal = ReadJournal(path, namespaceDirectory, out legacy);
                    if (journal.ModuleId != envelope.ModuleId || journal.TransactionId != envelope.TransactionId)
                        throw new JsonException();
                    if (legacy)
                    {
                        if (!ValidateLegacyMigrationSite(journal, installed, candidate, backup, failed, work))
                        {
                            required++;
                            continue;
                        }
                        Persist(journal);
                    }
                }
                catch { required++; continue; }
                ModuleUpdateTransactionResult result;
                if (journal.State is ModuleUpdateTransactionState.Committed or ModuleUpdateTransactionState.CleanupPending)
                    result = RecoverCommitted(journal, installed, work);
                else if (journal.State == ModuleUpdateTransactionState.RecoveryRequired)
                    result = Failure(journal.TransactionId, ModuleUpdateTransactionFailureCode.RecoveryRequired,
                        ModuleUpdateTransactionState.RecoveryRequired);
                else result = await RecoverIncompleteAsync(journal, installed, candidate, backup, failed, work, cancellationToken);
                results.Add(result);
                if (result.Succeeded && !result.CleanupPending) cleaned++;
                else if (result.RolledBack) recovered++;
                else required++;
            }
            foreach (var temp in Directory.Exists(namespaceDirectory)
                         ? Directory.EnumerateFiles(namespaceDirectory, "*.tmp-*") : [])
            {
                var corresponding = Path.Combine(namespaceDirectory, Path.GetFileName(temp).Split(".tmp-", 2)[0]);
                if (File.Exists(corresponding)) try { File.Delete(temp); } catch { }
                else required++;
            }
        }
        return new(recovered, cleaned, required, results);
    }

    private ModuleUpdateTransactionResult RecoverCommitted(TransactionJournal journal, string installed, string work)
    {
        if (!IdentityMatches(installed, journal.PromotedDirectoryIdentity ?? journal.CandidateDirectoryIdentity) ||
            !ValidateInstalledCandidate(installed, journal.PayloadFiles, journal.ModuleId, journal.TargetVersion,
                SecureWindowsFileSystem.HasOwnerMarker(installed, OwnershipMarkerName, journal.TransactionId)
                    ? journal.TransactionId : null))
        {
            Log("Unknown installed directory preserved", journal, ModuleUpdateTransactionFailureCode.RecoveryRequired);
            return Failure(journal.TransactionId, ModuleUpdateTransactionFailureCode.RecoveryRequired,
                ModuleUpdateTransactionState.RecoveryRequired);
        }
        return PostCommitCleanup(journal, installed, work);
    }

    private async Task<ModuleUpdateTransactionResult> RecoverIncompleteAsync(TransactionJournal journal,
        string installed, string candidate, string backup, string failed, string work, CancellationToken token)
    {
        if (journal.State is ModuleUpdateTransactionState.Preparing or ModuleUpdateTransactionState.Prepared or
            ModuleUpdateTransactionState.Quiescing or ModuleUpdateTransactionState.Quiesced)
        {
            if (Directory.Exists(backup) || Directory.Exists(failed) ||
                (journal.HadInstalledModule && !Directory.Exists(installed))) return RequireRecovery(journal);
            try
            {
                if (NeedsPreviousRuntimeRestore(journal))
                {
                    journal = UpdateProgress(journal,
                        journal.LifecycleProgress with { PreviousRuntimeRestoreStarted = true });
                    if (!await _runtime.RestorePreviousRuntimeStateAsync(
                            journal.ModuleId, journal.PreviousRuntimeState, token)) return RequireRecovery(journal);
                    journal = journal with
                    {
                        LifecycleProgress = journal.LifecycleProgress with
                        { PreviousRuntimeRestoredAfterRollback = true },
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                    _hooks?.JournalProgressPersistStarting?.Invoke(journal.LifecycleProgress);
                    Persist(journal);
                }
                journal = Advance(journal, ModuleUpdateTransactionState.RolledBack);
                SecureWindowsFileSystem.DeleteOwnedTree(work, OwnershipMarkerName, journal.TransactionId,
                    _physicalUserModulesRoot); DeleteJournal(journal);
                return new(false, journal.TransactionId, journal.State,
                    ModuleUpdateTransactionFailureCode.RecoveryRequired, RolledBack: true);
            }
            catch { return RequireRecovery(journal); }
        }
        if (journal.State is ModuleUpdateTransactionState.BackupStarted or ModuleUpdateTransactionState.BackupCreated or
            ModuleUpdateTransactionState.CandidatePromotionStarted or ModuleUpdateTransactionState.CandidatePromoted or
            ModuleUpdateTransactionState.Verifying or ModuleUpdateTransactionState.Verified or
            ModuleUpdateTransactionState.RuntimeRestoring or ModuleUpdateTransactionState.RollbackStarted)
        {
            if (Directory.Exists(installed) && Directory.Exists(backup) &&
                !IdentityMatches(installed, journal.PromotedDirectoryIdentity ?? journal.CandidateDirectoryIdentity))
                return RequireRecovery(journal);
            if (Directory.Exists(backup) &&
                (!IdentityMatches(backup, journal.InstalledDirectoryIdentity) || !ValidateInstalledSnapshot(backup, journal)))
                return RequireRecovery(journal);
            if (Directory.Exists(installed) && !Directory.Exists(backup) && journal.HadInstalledModule &&
                journal.State == ModuleUpdateTransactionState.BackupStarted &&
                !IdentityMatches(installed, journal.InstalledDirectoryIdentity)) return RequireRecovery(journal);
            return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                ModuleUpdateTransactionFailureCode.RecoveryRequired, token);
        }
        return RequireRecovery(journal);
    }

    private async Task<ModuleUpdateTransactionResult> RollbackAsync(TransactionJournal journal, string installed,
        string candidate, string backup, string failed, string work, ModuleUpdateTransactionFailureCode failure,
        CancellationToken cancellationToken, bool promotedRuntimeRestoreAttempted = false,
        bool promotedRuntimeMayBeRunning = false)
    {
        try
        {
            journal = journal with
            {
                LastFailureCode = failure,
                State = ModuleUpdateTransactionState.RollbackStarted,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            try
            {
                Persist(journal);
                _hooks?.StatePersisted?.Invoke(ModuleUpdateTransactionState.RollbackStarted);
            }
            catch { }
            journal = await QuiescePromotedRuntimeForRollbackAsync(journal, cancellationToken,
                promotedRuntimeRestoreAttempted, promotedRuntimeMayBeRunning);
            if (journal.State == ModuleUpdateTransactionState.RecoveryRequired) return RequireRecovery(journal);
            if (journal.HadInstalledModule && Directory.Exists(installed) && !Directory.Exists(backup) &&
                !IdentityMatches(installed, journal.InstalledDirectoryIdentity)) return RequireRecovery(journal);
            if (Directory.Exists(installed) && Directory.Exists(backup))
            {
                if (Directory.Exists(failed) || File.Exists(failed)) return RequireRecovery(journal);
                using var promotedLease = AcquireOwnedTreeLease(installed,
                    journal.PromotedDirectoryIdentity ?? journal.CandidateDirectoryIdentity,
                    journal.TransactionId);
                if (promotedLease is null || promotedLease.Snapshot.RootIdentity !=
                    (journal.PromotedDirectoryIdentity ?? journal.CandidateDirectoryIdentity))
                    return RequireRecovery(journal);
                _hooks?.TreeLeaseAcquired?.Invoke("installed-to-failed-candidate");
                promotedLease.VerifyAtPath(installed);
                Move(promotedLease, installed, failed, SecureWindowsFileSystem.DirectoryIdentity(work));
                promotedLease.VerifyAtPath(failed);
            }
            else if (Directory.Exists(installed) && !journal.HadInstalledModule)
            {
                if (Directory.Exists(failed) || File.Exists(failed)) return RequireRecovery(journal);
                using var promotedLease = AcquireOwnedTreeLease(installed,
                    journal.PromotedDirectoryIdentity ?? journal.CandidateDirectoryIdentity,
                    journal.TransactionId);
                if (promotedLease is null || promotedLease.Snapshot.RootIdentity !=
                    (journal.PromotedDirectoryIdentity ?? journal.CandidateDirectoryIdentity))
                    return RequireRecovery(journal);
                _hooks?.TreeLeaseAcquired?.Invoke("installed-to-failed-candidate");
                promotedLease.VerifyAtPath(installed);
                Move(promotedLease, installed, failed, SecureWindowsFileSystem.DirectoryIdentity(work));
                promotedLease.VerifyAtPath(failed);
            }
            if (Directory.Exists(backup))
            {
                if (Directory.Exists(installed) || File.Exists(installed)) return RequireRecovery(journal);
                using var backupLease = AcquireInstalledSnapshotLease(backup, journal);
                if (backupLease is null) return RequireRecovery(journal);
                _hooks?.TreeLeaseAcquired?.Invoke("backup-to-installed");
                backupLease.VerifyAtPath(backup);
                Move(backupLease, backup, installed, _userModulesRootIdentity);
                backupLease.VerifyAtPath(installed);
                if (!IdentityMatches(installed, journal.InstalledDirectoryIdentity)) return RequireRecovery(journal);
            }
            if (NeedsPreviousRuntimeRestore(journal))
            {
                journal = UpdateProgress(journal,
                    journal.LifecycleProgress with { PreviousRuntimeRestoreStarted = true });
                if (!await _runtime.RestorePreviousRuntimeStateAsync(
                        journal.ModuleId, journal.PreviousRuntimeState, cancellationToken)) return RequireRecovery(journal);
                journal = journal with
                {
                    LifecycleProgress = journal.LifecycleProgress with
                    { PreviousRuntimeRestoredAfterRollback = true },
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                try
                {
                    _hooks?.JournalProgressPersistStarting?.Invoke(journal.LifecycleProgress);
                    Persist(journal);
                }
                catch { return RequireRecovery(journal); }
            }
            journal = Advance(journal, ModuleUpdateTransactionState.RolledBack);
            SecureWindowsFileSystem.DeleteOwnedTree(work, OwnershipMarkerName, journal.TransactionId, _physicalUserModulesRoot);
            DeleteJournal(journal); return new(false, journal.TransactionId, journal.State, failure, RolledBack: true);
        }
        catch { return RequireRecovery(journal); }
    }

    private ModuleUpdateTransactionResult PostCommitCleanup(TransactionJournal journal, string installed, string work)
    {
        Log("Post-commit cleanup started", journal);
        try
        {
            if (!IdentityMatches(installed, journal.PromotedDirectoryIdentity ?? journal.CandidateDirectoryIdentity))
                return PostCommitPending(journal);
            if (SecureWindowsFileSystem.HasOwnerMarker(installed, OwnershipMarkerName, journal.TransactionId))
            {
                _hooks?.MarkerDeleteStarting?.Invoke();
                SecureWindowsFileSystem.DeleteOwnerMarker(installed, OwnershipMarkerName, journal.TransactionId);
            }
            if (!ValidateInstalledCandidate(installed, journal.PayloadFiles, journal.ModuleId, journal.TargetVersion, null))
                return PostCommitPending(journal);
            _hooks?.BackupCleanupStarting?.Invoke(); _hooks?.WorkDeleteStarting?.Invoke();
            SecureWindowsFileSystem.DeleteOwnedTree(work, OwnershipMarkerName, journal.TransactionId, _physicalUserModulesRoot);
            DeleteJournal(journal);
            return new(true, journal.TransactionId, ModuleUpdateTransactionState.Committed,
                ModuleUpdateTransactionFailureCode.None);
        }
        catch { return PostCommitPending(journal); }
    }

    private ModuleUpdateTransactionResult PostCommitPending(TransactionJournal journal)
    {
        try { journal = Advance(journal, ModuleUpdateTransactionState.CleanupPending); }
        catch { }
        Log("Post-commit cleanup pending", journal);
        return new(true, journal.TransactionId, ModuleUpdateTransactionState.CleanupPending,
            ModuleUpdateTransactionFailureCode.None, CleanupPending: true);
    }

    private void BuildCandidate(QmodVerifiedStagingAttestation attestation, string candidate, Guid transactionId,
        IReadOnlyList<QmodStagedFile> payload)
    {
        Directory.CreateDirectory(candidate); SecureWindowsFileSystem.WriteOwnerMarker(candidate, OwnershipMarkerName, transactionId);
        var completedFiles = 0;
        foreach (var file in payload)
        {
            var source = SecureWindowsFileSystem.SafeCombine(attestation.Directory, file.RelativePath);
            var destination = SecureWindowsFileSystem.SafeCombine(candidate, file.RelativePath);
            EnsureCandidateParents(candidate, Path.GetDirectoryName(destination)!);
            using var sourceHandle = SecureWindowsFileSystem.OpenStableRead(source, attestation.PhysicalDirectoryIdentity);
            var before = RandomAccess.GetLength(sourceHandle); if (before != file.Size) throw new IOException("Source length changed.");
            using var sourceStream = new FileStream(sourceHandle, FileAccess.Read, 65536, false);
            using var destinationStream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; long copied = 0;
            while (true)
            {
                var read = sourceStream.Read(buffer); if (read == 0) break;
                copied = checked(copied + read); if (copied > file.Size) throw new IOException("Source grew.");
                hash.AppendData(buffer, 0, read); destinationStream.Write(buffer, 0, read);
            }
            destinationStream.Flush(true);
            if (copied != file.Size || RandomAccess.GetLength(sourceHandle) != before ||
                !Convert.ToHexString(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Stable source verification failed.");
            if (++completedFiles == 1)
                _hooks?.CrashPoint?.Invoke(ModuleUpdateTransactionCrashPoint.CandidateCopyInProgress);
        }
        if (!ValidatePayloadTree(candidate, payload, attestation.ModuleId, attestation.TargetVersion, transactionId))
            throw new IOException("Candidate verification failed.");
    }

    private bool ReattestVerifiedStaging(QmodVerifiedStagingAttestation attestation)
    {
        try
        {
            if (!IsSha256(attestation.PackageSha256) || !IsSha256(attestation.OfficialReleaseIdentityHash) ||
                !SecureWindowsFileSystem.PhysicalDirectory(attestation.Directory).Equals(
                    attestation.PhysicalDirectoryIdentity, StringComparison.OrdinalIgnoreCase) ||
                !SecureWindowsFileSystem.IsWithin(attestation.PhysicalVerifiedRootIdentity,
                    attestation.PhysicalDirectoryIdentity)) return false;
            var files = attestation.Files.Concat([attestation.StagingMetadataFile]).ToArray();
            return ValidatePayloadTree(attestation.Directory, files, attestation.ModuleId,
                attestation.TargetVersion, null, requirePackageManifest: true);
        }
        catch { return false; }
    }

    private bool ValidateInstalledCandidate(string installed, IReadOnlyList<QmodStagedFile> expected,
        string moduleId, string version, Guid? marker)
    {
        using var lease = AcquirePayloadTreeLease(installed, expected, moduleId, version, marker);
        return lease is not null;
    }

    private static bool ValidatePayloadTree(string root, IReadOnlyList<QmodStagedFile> expected, string moduleId,
        string version, Guid? marker, bool requirePackageManifest = false)
    {
        using var lease = AcquirePayloadTreeLease(root, expected, moduleId, version, marker,
            requirePackageManifest);
        return lease is not null;
    }

    private static SecureTreeLease? AcquirePayloadTreeLease(string root, IReadOnlyList<QmodStagedFile> expected,
        string moduleId, string version, Guid? marker, bool requirePackageManifest = false)
    {
        SecureTreeLease? lease = null;
        try
        {
            lease = SecureWindowsFileSystem.AcquireStableTreeLease(root);
            var expectedMap = new Dictionary<string, QmodStagedFile>(StringComparer.Ordinal);
            var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
            var collision = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in expected)
            {
                var relative = NormalizeRelative(file.RelativePath);
                if (!expectedMap.TryAdd(relative, file) || !collision.Add(CollisionKey(relative)) || !IsSha256(file.Sha256))
                    return DisposeLease();
                var parent = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar));
                while (!string.IsNullOrEmpty(parent))
                {
                    expectedDirectories.Add(parent.Replace('\\', '/')); parent = Path.GetDirectoryName(parent);
                }
            }
            if (marker is not null) collision.Add(CollisionKey(OwnershipMarkerName));
            var foundFiles = new HashSet<string>(StringComparer.Ordinal); var foundDirs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var treeEntry in lease.Snapshot.Entries)
            {
                if (treeEntry.IsReparsePoint) return DisposeLease();
                var relative = NormalizeRelative(treeEntry.RelativePath);
                if (!collision.Contains(CollisionKey(relative)) && !collision.Add(CollisionKey(relative))) return DisposeLease();
                if (treeEntry.IsDirectory)
                {
                    if (!expectedDirectories.Contains(relative)) return DisposeLease();
                    foundDirs.Add(relative); continue;
                }
                if (marker is not null && relative == OwnershipMarkerName)
                {
                    if (!lease.TryGetVerifiedFileBytes(OwnershipMarkerName, out var ownerBytes) ||
                        ownerBytes.Length != 36 || ownerBytes[0] == 0xEF ||
                        Encoding.UTF8.GetString(ownerBytes) != marker.Value.ToString("D")) return DisposeLease();
                    foundFiles.Add(relative); continue;
                }
                if (!expectedMap.TryGetValue(relative, out var record)) return DisposeLease();
                if (treeEntry.Length != record.Size ||
                    !treeEntry.Sha256.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase)) return DisposeLease();
                foundFiles.Add(relative);
            }
            if (!foundDirs.SetEquals(expectedDirectories) || expectedMap.Keys.Any(key => !foundFiles.Contains(key)) ||
                (marker is not null && !foundFiles.Contains(OwnershipMarkerName))) return DisposeLease();
            if (requirePackageManifest && !expectedMap.ContainsKey(QmodPackageStagingService.PackageManifestName))
                return DisposeLease();
            if (!lease.TryGetVerifiedFileBytes("module.json", out var manifestBytes) ||
                !ValidateManifest(manifestBytes, moduleId, version, expectedMap)) return DisposeLease();
            lease.VerifyAtPath(root);
            return lease;
        }
        catch { lease?.Dispose(); return null; }

        SecureTreeLease? DisposeLease()
        {
            lease?.Dispose();
            lease = null;
            return null;
        }
    }

    private static bool ValidateManifest(byte[]? bytes, string moduleId, string version,
        IReadOnlyDictionary<string, QmodStagedFile> expected)
    {
        if (bytes is null || bytes.Length > 64 * 1024) return false;
        using var document = JsonDocument.Parse(bytes); var root = document.RootElement;
        var id = root.GetProperty("id").GetString(); var actualVersion = root.GetProperty("version").GetString();
        var entry = NormalizeRelative(root.GetProperty("entry").GetString()!);
        return id == moduleId && actualVersion == version && expected.ContainsKey(entry) &&
               SecureModuleIdentity.Validate(id) == ModuleIdentityFailure.None && SemanticVersion.TryParse(actualVersion!, out _);
    }

    private static string NormalizeRelative(string value)
    {
        var original = value.Replace('\\', '/'); var normalized = original.Normalize(NormalizationForm.FormC);
        if (original != normalized) throw new IOException("Non-canonical Unicode path rejected.");
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') ||
            normalized.Any(ch => char.IsControl(ch) || ch == ':') ||
            normalized.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' ')))
            throw new IOException("Unsafe relative path.");
        return normalized;
    }
    private static string CollisionKey(string relative) => relative.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static IReadOnlyList<QmodStagedFile> Payload(QmodVerifiedStagingAttestation attestation) =>
        attestation.Files.Where(file => file.RelativePath is not QmodPackageStagingService.PackageManifestName).ToArray();

    private static InstalledSnapshot? ReadInstalledSnapshot(string installed)
    {
        SecureTreeLease? lease = null;
        try
        {
            lease = SecureWindowsFileSystem.AcquireStableTreeLease(installed);
            if (!lease.TryGetVerifiedFileBytes("module.json", out var manifestBytes) || manifestBytes.Length == 0)
                return null;
            using var document = JsonDocument.Parse(manifestBytes); var root = document.RootElement;
            var id = root.GetProperty("id").GetString(); var version = root.GetProperty("version").GetString();
            var entry = NormalizeRelative(root.GetProperty("entry").GetString()!);
            if (SecureModuleIdentity.Validate(id) != ModuleIdentityFailure.None ||
                !SemanticVersion.TryParse(version!, out _) ||
                !lease.ContainsOrdinaryFile(entry)) return null;
            var files = new List<QmodStagedFile>();
            var collisions = new HashSet<string>(StringComparer.Ordinal);
            var treeSnapshot = lease.Snapshot;
            foreach (var treeEntry in treeSnapshot.Entries)
            {
                if (treeEntry.IsReparsePoint) return null;
                var relative = NormalizeRelative(treeEntry.RelativePath);
                if (relative.Equals(OwnershipMarkerName, StringComparison.OrdinalIgnoreCase)) return null;
                if (!collisions.Add(CollisionKey(relative))) return null;
                if (treeEntry.IsDirectory) continue;
                files.Add(new(relative, treeEntry.Length, treeEntry.Sha256));
            }
            if (!files.Any(file => file.RelativePath == entry)) return null;
            lease.VerifyAtPath(installed);
            return new(id!, version!, entry, treeSnapshot.RootIdentity,
                files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray());
        }
        catch { return null; }
        finally { lease?.Dispose(); }
    }

    private static bool IdentityMatches(string path, SecureDirectoryIdentity? expected)
    {
        if (expected is null || !Directory.Exists(path)) return false;
        try { return SecureWindowsFileSystem.DirectoryIdentity(path) == expected.Value; }
        catch { return false; }
    }

    private static bool ValidateInstalledSnapshot(string path, TransactionJournal journal)
    {
        var snapshot = ReadInstalledSnapshot(path);
        return snapshot is not null && snapshot.ModuleId == journal.ModuleId &&
               snapshot.Version == journal.SourceVersion &&
               snapshot.DirectoryIdentity == journal.InstalledDirectoryIdentity &&
               snapshot.Files.SequenceEqual(journal.InstalledFiles);
    }

    private static SecureTreeLease? AcquireInstalledSnapshotLease(string path, TransactionJournal journal)
    {
        SecureTreeLease? lease = null;
        try
        {
            lease = SecureWindowsFileSystem.AcquireStableTreeLease(path);
            if (!lease.TryGetVerifiedFileBytes("module.json", out var manifestBytes) || manifestBytes.Length == 0)
                return DisposeLease();
            using var document = JsonDocument.Parse(manifestBytes);
            var root = document.RootElement;
            var id = root.GetProperty("id").GetString();
            var version = root.GetProperty("version").GetString();
            var entry = NormalizeRelative(root.GetProperty("entry").GetString()!);
            var files = lease.Snapshot.Entries.Where(item => !item.IsDirectory && !item.IsReparsePoint)
                .Select(item => new QmodStagedFile(NormalizeRelative(item.RelativePath), item.Length, item.Sha256))
                .OrderBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
            if (lease.Snapshot.Entries.Any(item => item.IsReparsePoint) || id != journal.ModuleId ||
                version != journal.SourceVersion || !lease.ContainsOrdinaryFile(entry) ||
                lease.Snapshot.RootIdentity != journal.InstalledDirectoryIdentity ||
                !files.SequenceEqual(journal.InstalledFiles)) return DisposeLease();
            lease.VerifyAtPath(path);
            return lease;
        }
        catch { lease?.Dispose(); return null; }

        SecureTreeLease? DisposeLease()
        {
            lease?.Dispose();
            lease = null;
            return null;
        }
    }

    private static SecureTreeLease? AcquireOwnedTreeLease(string path, SecureDirectoryIdentity? expectedIdentity,
        Guid transactionId)
    {
        SecureTreeLease? lease = null;
        try
        {
            if (expectedIdentity is null) return null;
            lease = SecureWindowsFileSystem.AcquireStableTreeLease(path);
            if (lease.Snapshot.RootIdentity != expectedIdentity ||
                lease.Snapshot.Entries.Any(item => item.IsReparsePoint) ||
                !lease.TryGetVerifiedFileBytes(OwnershipMarkerName, out var bytes) || bytes.Length != 36 ||
                bytes[0] == 0xEF || Encoding.UTF8.GetString(bytes) != transactionId.ToString("D"))
            {
                lease.Dispose();
                return null;
            }
            lease.VerifyAtPath(path);
            return lease;
        }
        catch { lease?.Dispose(); return null; }
    }

    private void EnsureCandidateParents(string root, string target)
    {
        var relative = Path.GetRelativePath(root, target); var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment); if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            SecureWindowsFileSystem.EnsureOrdinaryDirectory(current);
            if (!SecureWindowsFileSystem.IsWithin(SecureWindowsFileSystem.PhysicalDirectory(root),
                    SecureWindowsFileSystem.PhysicalDirectory(current))) throw new IOException("Candidate parent escaped root.");
        }
    }

    private void Move(SecureTreeLease sourceLease, string source, string destination,
        SecureDirectoryIdentity expectedDestinationParentIdentity)
    {
        if (!Directory.Exists(source) || SecureWindowsFileSystem.IsReparsePath(source) ||
            Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Unsafe move.");
        if (!SecureWindowsFileSystem.IsWithin(_physicalUserModulesRoot, SecureWindowsFileSystem.PhysicalDirectory(source)) ||
            !SecureWindowsFileSystem.IsWithin(_physicalUserModulesRoot,
                SecureWindowsFileSystem.PhysicalDirectory(Path.GetDirectoryName(destination)!))) throw new IOException("Move escaped root.");
        SecureWindowsFileSystem.RenameLeasedDirectory(sourceLease, source, destination,
            _physicalUserModulesRoot, expectedDestinationParentIdentity,
            stage => _hooks?.DirectoryRename?.Invoke(source, destination, stage));
    }

    private TransactionJournal UpdateProgress(TransactionJournal journal, LifecycleProgress progress)
    {
        var updated = journal with { LifecycleProgress = progress, UpdatedAtUtc = DateTimeOffset.UtcNow };
        _hooks?.JournalProgressPersistStarting?.Invoke(progress);
        Persist(updated);
        return updated;
    }
    private TransactionJournal Advance(TransactionJournal journal, ModuleUpdateTransactionState state)
    {
        var updated = journal with { State = state, UpdatedAtUtc = DateTimeOffset.UtcNow };
        Persist(updated); _hooks?.StatePersisted?.Invoke(state); return updated;
    }
    private static bool NeedsPreviousRuntimeRestore(TransactionJournal journal) =>
        journal.HadInstalledModule && !journal.LifecycleProgress.PreviousRuntimeRestoredAfterRollback &&
        (journal.LifecycleProgress.WindowsClosed || journal.LifecycleProgress.Deactivated ||
         journal.LifecycleProgress.Unloaded || journal.LifecycleProgress.PromotedRuntimeRestoreStarted ||
         journal.LifecycleProgress.PromotedRuntimeRestored || journal.LifecycleProgress.PreviousRuntimeRestoreStarted);

    private async Task<TransactionJournal> QuiescePromotedRuntimeForRollbackAsync(
        TransactionJournal journal, CancellationToken cancellationToken,
        bool promotedRuntimeRestoreAttempted, bool promotedRuntimeMayBeRunning)
    {
        var shouldInspect = !journal.LifecycleProgress.PreviousRuntimeRestoredAfterRollback &&
                            (promotedRuntimeRestoreAttempted || promotedRuntimeMayBeRunning ||
                            journal.LifecycleProgress.PromotedRuntimeRestoreStarted ||
                            journal.LifecycleProgress.PromotedRuntimeRestored ||
                            journal.State == ModuleUpdateTransactionState.RuntimeRestoring);
        if (!shouldInspect) return journal;
        try
        {
            var current = await _runtime.GetRuntimeStateAsync(journal.ModuleId, cancellationToken);
            if (current.HasWindows &&
                !await _runtime.RequestCloseWindowsAsync(journal.ModuleId, cancellationToken)) return Recovery(journal);
            if (current.IsActive &&
                !await _runtime.DeactivateAsync(journal.ModuleId, cancellationToken)) return Recovery(journal);
            if (current.IsLoaded &&
                !await _runtime.UnloadAsync(journal.ModuleId, cancellationToken)) return Recovery(journal);
            if (!await _runtime.VerifyUnloadedAsync(journal.ModuleId, cancellationToken)) return Recovery(journal);
            journal = journal with
            {
                LifecycleProgress = journal.LifecycleProgress with
                { PromotedRuntimeQuiescedForRollback = true },
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            try
            {
                _hooks?.JournalProgressPersistStarting?.Invoke(journal.LifecycleProgress);
                Persist(journal);
            }
            catch { }
            return journal;
        }
        catch { return Recovery(journal); }
    }

    private TransactionJournal Recovery(TransactionJournal journal)
    {
        try { return Advance(journal, ModuleUpdateTransactionState.RecoveryRequired); }
        catch { return journal with { State = ModuleUpdateTransactionState.RecoveryRequired }; }
    }

    private bool ValidateLegacyMigrationSite(TransactionJournal journal, string installed, string candidate,
        string backup, string failed, string work)
    {
        try
        {
            if (journal.State is ModuleUpdateTransactionState.RecoveryRequired or ModuleUpdateTransactionState.Failed)
                return false;
            var workExists = Directory.Exists(work);
            if (File.Exists(work) || workExists &&
                (SecureWindowsFileSystem.IsReparsePath(work) ||
                 !SecureWindowsFileSystem.IsWithin(_physicalUserModulesRoot,
                     SecureWindowsFileSystem.PhysicalDirectory(work)) ||
                 !SecureWindowsFileSystem.HasOwnerMarker(work, OwnershipMarkerName, journal.TransactionId)))
                return false;
            if (!workExists && (Directory.Exists(candidate) || Directory.Exists(backup) || Directory.Exists(failed)))
                return false;
            if (Directory.Exists(candidate) && !OwnedIdentityMatches(candidate,
                    journal.CandidateDirectoryIdentity, journal.TransactionId)) return false;
            if (Directory.Exists(failed) && !OwnedIdentityMatches(failed,
                    journal.PromotedDirectoryIdentity ?? journal.CandidateDirectoryIdentity,
                    journal.TransactionId)) return false;
            if (Directory.Exists(backup) &&
                (!IdentityMatches(backup, journal.InstalledDirectoryIdentity) ||
                 !ValidateInstalledSnapshot(backup, journal))) return false;

            var early = journal.State is ModuleUpdateTransactionState.Preparing or
                ModuleUpdateTransactionState.Prepared or ModuleUpdateTransactionState.Quiescing or
                ModuleUpdateTransactionState.Quiesced;
            if (early)
            {
                if (Directory.Exists(backup) || Directory.Exists(failed)) return false;
                return journal.HadInstalledModule
                    ? IdentityMatches(installed, journal.InstalledDirectoryIdentity) &&
                      ValidateInstalledSnapshot(installed, journal)
                    : !Directory.Exists(installed) && !File.Exists(installed);
            }

            if (journal.State is ModuleUpdateTransactionState.Committed or
                ModuleUpdateTransactionState.CleanupPending)
                return IdentityMatches(installed,
                           journal.PromotedDirectoryIdentity ?? journal.CandidateDirectoryIdentity) &&
                       ValidateInstalledCandidate(installed, journal.PayloadFiles, journal.ModuleId,
                           journal.TargetVersion,
                           SecureWindowsFileSystem.HasOwnerMarker(installed, OwnershipMarkerName,
                               journal.TransactionId) ? journal.TransactionId : null);

            if (!Directory.Exists(installed)) return Directory.Exists(backup);
            if (Directory.Exists(backup) || !journal.HadInstalledModule)
                return OwnedIdentityMatches(installed,
                    journal.PromotedDirectoryIdentity ?? journal.CandidateDirectoryIdentity,
                    journal.TransactionId);
            return IdentityMatches(installed, journal.InstalledDirectoryIdentity) &&
                   ValidateInstalledSnapshot(installed, journal);
        }
        catch { return false; }

        static bool OwnedIdentityMatches(string path, SecureDirectoryIdentity? identity, Guid transactionId) =>
            identity is not null && SecureWindowsFileSystem.DirectoryIdentity(path) == identity &&
            SecureWindowsFileSystem.HasOwnerMarker(path, OwnershipMarkerName, transactionId);
    }

    private void Persist(TransactionJournal journal)
    {
        _hooks?.JournalPersistStarting?.Invoke(journal.State);
        var directory = JournalNamespace(journal.ModuleId);
        var namespaceIdentity = EnsureJournalNamespace(directory);
        using var namespaceLease = SecureWindowsFileSystem.AcquireDirectoryLease(
            directory, _physicalJournalRoot, namespaceIdentity);
        var final = JournalPath(journal.ModuleId, journal.TransactionId);
        var temp = final + ".tmp-" + Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalOptions);
        SecureWindowsFileSystem.WriteAndRenameFile(temp, bytes, namespaceLease, Path.GetFileName(final),
            _physicalJournalRoot, replaceIfExists: true, _hooks?.JournalTempWritten);
    }

    private TransactionJournal ReadJournal(string path, string namespaceDirectory, out bool legacy)
    {
        var bytes = ReadJournalBytes(path, namespaceDirectory);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        ValidateObject(document.RootElement,
        ["schemaVersion", "transactionId", "environmentIdentity", "moduleId", "sourceVersion", "targetVersion",
         "moduleApiVersion", "packageSha256", "verifiedStagingIdentity", "userModulesRootIdentity", "state",
         "previousRuntimeState", "lifecycleProgress", "attempt", "lastFailureCode", "hadInstalledModule",
          "payloadFiles", "installedFiles", "installedDirectoryIdentity", "candidateDirectoryIdentity",
          "backupDirectoryIdentity", "promotedDirectoryIdentity", "startedAtUtc", "updatedAtUtc"]);
        ValidateObject(document.RootElement.GetProperty("previousRuntimeState"),
            ["hasWindows", "isActive", "isLoaded", "hasStartupAuthorization"]);
        var schema = document.RootElement.GetProperty("schemaVersion").GetInt32();
        TransactionJournal journal;
        if (schema == JournalSchema)
        {
            ValidateObject(document.RootElement.GetProperty("lifecycleProgress"),
                ["windowsClosed", "deactivated", "unloaded", "promotedRuntimeRestoreStarted",
                 "promotedRuntimeRestored", "promotedRuntimeQuiescedForRollback",
                 "previousRuntimeRestoreStarted", "previousRuntimeRestoredAfterRollback"]);
            journal = JsonSerializer.Deserialize<TransactionJournal>(document.RootElement, JournalOptions) ??
                      throw new JsonException();
            legacy = false;
        }
        else if (schema == LegacyJournalSchema)
        {
            ValidateObject(document.RootElement.GetProperty("lifecycleProgress"),
                ["windowsClosed", "deactivated", "unloaded", "runtimeRestored"]);
            var old = JsonSerializer.Deserialize<LegacyTransactionJournal>(document.RootElement, JournalOptions) ??
                      throw new JsonException();
            var restoreStarted = old.LifecycleProgress.RuntimeRestored ||
                                 old.State == ModuleUpdateTransactionState.RuntimeRestoring ||
                                 old.State == ModuleUpdateTransactionState.RollbackStarted;
            journal = new(old.TransactionId, old.EnvironmentIdentity, old.ModuleId, old.SourceVersion,
                old.TargetVersion, old.ModuleApiVersion, old.PackageSha256, old.VerifiedStagingIdentity,
                old.UserModulesRootIdentity, old.State, old.PreviousRuntimeState,
                new(old.LifecycleProgress.WindowsClosed, old.LifecycleProgress.Deactivated,
                    old.LifecycleProgress.Unloaded, restoreStarted, old.LifecycleProgress.RuntimeRestored,
                    false, false, false), old.Attempt, old.LastFailureCode, old.HadInstalledModule,
                old.PayloadFiles, old.InstalledFiles, old.InstalledDirectoryIdentity,
                old.CandidateDirectoryIdentity, old.BackupDirectoryIdentity, old.PromotedDirectoryIdentity,
                old.StartedAtUtc, old.UpdatedAtUtc);
            legacy = true;
        }
        else throw new JsonException();
        foreach (var file in document.RootElement.GetProperty("payloadFiles").EnumerateArray())
            ValidateObject(file, ["relativePath", "size", "sha256"]);
        foreach (var file in document.RootElement.GetProperty("installedFiles").EnumerateArray())
            ValidateObject(file, ["relativePath", "size", "sha256"]);
        foreach (var name in new[] { "installedDirectoryIdentity", "candidateDirectoryIdentity",
                     "backupDirectoryIdentity", "promotedDirectoryIdentity" })
            if (document.RootElement.GetProperty(name).ValueKind != JsonValueKind.Null)
                ValidateObject(document.RootElement.GetProperty(name), ["volumeSerialNumber", "fileId"]);
        var fileId = Path.GetFileNameWithoutExtension(path);
        if (journal.SchemaVersion != JournalSchema || journal.TransactionId.ToString("N") != fileId ||
            journal.EnvironmentIdentity != _environmentIdentity || SecureModuleIdentity.Validate(journal.ModuleId) != ModuleIdentityFailure.None ||
            !Path.GetFileName(namespaceDirectory).Equals(JournalNamespaceName(journal.ModuleId), StringComparison.Ordinal) ||
            journal.UserModulesRootIdentity != SecureWindowsFileSystem.HashIdentity(_physicalUserModulesRoot) ||
            journal.ModuleApiVersion != _moduleApiVersion || !IsSha256(journal.PackageSha256) ||
            !IsSha256(journal.VerifiedStagingIdentity) || !IsSha256(journal.UserModulesRootIdentity) ||
            !SemanticVersion.TryParse(journal.TargetVersion, out var target) ||
            (journal.SourceVersion.Length != 0 && (!SemanticVersion.TryParse(journal.SourceVersion, out var source) || target!.CompareTo(source!) <= 0)) ||
            journal.HadInstalledModule != (journal.SourceVersion.Length != 0) ||
            !Enum.IsDefined(journal.State) || !Enum.IsDefined(journal.LastFailureCode) || journal.Attempt < 1 ||
             journal.StartedAtUtc == default || journal.UpdatedAtUtc < journal.StartedAtUtc || journal.PayloadFiles.Count == 0 ||
             journal.HadInstalledModule != (journal.InstalledDirectoryIdentity is not null) ||
             (journal.HadInstalledModule && journal.InstalledFiles.Count == 0) ||
             !ValidDirectoryIdentity(journal.InstalledDirectoryIdentity) ||
             !ValidDirectoryIdentity(journal.CandidateDirectoryIdentity) ||
             !ValidDirectoryIdentity(journal.BackupDirectoryIdentity) ||
             !ValidDirectoryIdentity(journal.PromotedDirectoryIdentity) ||
             (journal.State >= ModuleUpdateTransactionState.Prepared && journal.CandidateDirectoryIdentity is null) ||
             (journal.State is >= ModuleUpdateTransactionState.BackupCreated and < ModuleUpdateTransactionState.RollbackStarted &&
              journal.HadInstalledModule && journal.BackupDirectoryIdentity is null) ||
             (journal.State is >= ModuleUpdateTransactionState.CandidatePromoted and < ModuleUpdateTransactionState.RollbackStarted &&
              journal.PromotedDirectoryIdentity is null))
            throw new JsonException();
        foreach (var file in journal.PayloadFiles.Concat(journal.InstalledFiles))
            if (file.Size < 0 || !IsSha256(file.Sha256) || NormalizeRelative(file.RelativePath) != file.RelativePath.Replace('\\', '/'))
                throw new JsonException();
        return journal;
    }

    private JournalEnvelope ReadJournalEnvelope(string path, string namespaceDirectory)
    {
        var bytes = ReadJournalBytes(path, namespaceDirectory);
        using var document = JsonDocument.Parse(bytes,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var schema = root.GetProperty("schemaVersion").GetInt32();
        var transactionId = root.GetProperty("transactionId").GetGuid();
        var moduleId = root.GetProperty("moduleId").GetString() ?? throw new JsonException();
        if (schema is not (JournalSchema or LegacyJournalSchema) ||
            SecureModuleIdentity.Validate(moduleId) != ModuleIdentityFailure.None ||
            transactionId.ToString("N") != Path.GetFileNameWithoutExtension(path) ||
            !Path.GetFileName(namespaceDirectory).Equals(JournalNamespaceName(moduleId), StringComparison.Ordinal))
            throw new JsonException();
        return new(transactionId, moduleId);
    }

    private byte[] ReadJournalBytes(string path, string namespaceDirectory)
    {
        ValidateJournalRoot();
        var namespaceIdentity = SecureWindowsFileSystem.DirectoryIdentity(namespaceDirectory);
        using var namespaceLease = SecureWindowsFileSystem.AcquireDirectoryLease(
            namespaceDirectory, _physicalJournalRoot, namespaceIdentity);
        if (SecureWindowsFileSystem.IsReparsePath(path) || !Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(
                Path.GetFullPath(namespaceDirectory), StringComparison.OrdinalIgnoreCase)) throw new JsonException();
        byte[] bytes;
        using (var handle = SecureWindowsFileSystem.OpenStableRead(path, _physicalJournalRoot))
        {
            var length = RandomAccess.GetLength(handle);
            if (length is <= 0 or > 4 * 1024 * 1024) throw new JsonException();
            bytes = new byte[checked((int)length)];
            if (RandomAccess.Read(handle, bytes, 0) != bytes.Length || RandomAccess.GetLength(handle) != length)
                throw new JsonException();
        }
        if (SecureWindowsFileSystem.DirectoryIdentity(namespaceDirectory) != namespaceIdentity) throw new JsonException();
        namespaceLease.Verify();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            throw new JsonException();
        return bytes;
    }

    private static bool ValidDirectoryIdentity(SecureDirectoryIdentity? identity) =>
        identity is null || identity.Value.VolumeSerialNumber != 0 && identity.Value.FileId is { Length: 32 } &&
        identity.Value.FileId.All(char.IsAsciiHexDigit);

    private static void ValidateObject(JsonElement element, IReadOnlyCollection<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException();
        var allowed = new HashSet<string>(names, StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new JsonException();
        if (!seen.SetEquals(allowed)) throw new JsonException();
    }

    private IEnumerable<string> ModuleJournalEntries(string moduleId)
    {
        ValidateJournalRoot();
        var directory = JournalNamespace(moduleId); if (!Directory.Exists(directory)) return [];
        _ = EnsureJournalNamespace(directory);
        return Directory.EnumerateFileSystemEntries(directory).ToArray();
    }
    private void ValidateJournalRoot()
    {
        if (SecureWindowsFileSystem.DirectoryIdentity(_journalRoot) != _journalRootIdentity ||
            !SecureWindowsFileSystem.PhysicalDirectory(_journalRoot).Equals(_physicalJournalRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Journal root identity changed.");
    }
    private SecureDirectoryIdentity EnsureJournalNamespace(string directory)
    {
        ValidateJournalRoot();
        SecureWindowsFileSystem.CreateOrdinaryDirectoryTree(directory);
        var identity = SecureWindowsFileSystem.DirectoryIdentity(directory);
        var expected = _journalNamespaceIdentities.GetOrAdd(Path.GetFileName(directory), identity);
        if (identity != expected) throw new IOException("Journal namespace identity changed.");
        if (!SecureWindowsFileSystem.IsWithin(_physicalJournalRoot, SecureWindowsFileSystem.PhysicalDirectory(directory)))
            throw new IOException("Journal namespace escaped root.");
        return identity;
    }
    private string JournalNamespace(string moduleId) => Path.Combine(_journalRoot, JournalNamespaceName(moduleId));
    private string JournalNamespaceName(string moduleId) => SecureWindowsFileSystem.HashIdentity(
        $"{_physicalUserModulesRoot.ToUpperInvariant()}\0{_environmentIdentity}\0{moduleId}");
    private string JournalPath(string moduleId, Guid transactionId) =>
        Path.Combine(JournalNamespace(moduleId), transactionId.ToString("N") + ".json");
    private string InstalledPath(string moduleId) => SecureWindowsFileSystem.SafeCombine(_userModulesRoot, moduleId);
    private string WorkPath(Guid transactionId) => SecureWindowsFileSystem.SafeCombine(_workRoot, transactionId.ToString("N"));
    private async Task<CrashRecoverableFileLock> AcquireTransactionLockAsync(string moduleId, CancellationToken token)
    {
        var identity = SecureWindowsFileSystem.HashIdentity($"{_physicalUserModulesRoot.ToUpperInvariant()}\0{_environmentIdentity}\0{moduleId}");
        return await SecureWindowsFileSystem.AcquireLockAsync(Path.Combine(_locksRoot, identity + ".transaction.lock"),
            _physicalLocksRoot, _locksRootIdentity, token, _hooks?.LockParentLeaseAcquired);
    }
    internal string GetTransactionLockPathForTest(string moduleId)
    {
        var identity = SecureWindowsFileSystem.HashIdentity($"{_physicalUserModulesRoot.ToUpperInvariant()}\0{_environmentIdentity}\0{moduleId}");
        return Path.Combine(_locksRoot, identity + ".transaction.lock");
    }
    internal string GetJournalNamespaceForTest(string moduleId) => JournalNamespace(moduleId);
    private void DeleteJournal(TransactionJournal journal)
    {
        _hooks?.JournalDeleteStarting?.Invoke(); var path = JournalPath(journal.ModuleId, journal.TransactionId);
        var directory = JournalNamespace(journal.ModuleId); var identity = EnsureJournalNamespace(directory);
        if (File.Exists(path))
        {
            using var handle = SecureWindowsFileSystem.OpenStableRead(path, _physicalJournalRoot);
            if (SecureWindowsFileSystem.DirectoryIdentity(directory) != identity) throw new IOException("Journal namespace changed.");
            handle.Dispose(); File.Delete(path);
        }
        if (!Directory.EnumerateFileSystemEntries(directory).Any() &&
            SecureWindowsFileSystem.DirectoryIdentity(directory) == identity) Directory.Delete(directory, false);
    }
    private void TryDeleteWork(string work, Guid id)
    { try { SecureWindowsFileSystem.DeleteOwnedTree(work, OwnershipMarkerName, id, _physicalUserModulesRoot); } catch { } }
    private async Task<ModuleUpdateTransactionResult> RejectAsync(TransactionJournal journal, string work,
        ModuleUpdateTransactionFailureCode failure)
    {
        journal = Advance(journal with { LastFailureCode = failure }, ModuleUpdateTransactionState.Failed);
        TryDeleteWork(work, journal.TransactionId); try { DeleteJournal(journal); } catch { }
        await Task.CompletedTask; return Failure(journal.TransactionId, failure, journal.State);
    }
    private ModuleUpdateTransactionResult RequireRecovery(TransactionJournal journal)
    {
        try { journal = Advance(journal, ModuleUpdateTransactionState.RecoveryRequired); } catch { }
        Log("Installed ownership rejected", journal, ModuleUpdateTransactionFailureCode.RecoveryRequired);
        return Failure(journal.TransactionId, ModuleUpdateTransactionFailureCode.RecoveryRequired,
            ModuleUpdateTransactionState.RecoveryRequired);
    }
    private void Log(string name, TransactionJournal journal, ModuleUpdateTransactionFailureCode failure = ModuleUpdateTransactionFailureCode.None)
    { try { _log?.Invoke(new(name, journal.ModuleId, journal.SourceVersion, journal.TargetVersion, journal.TransactionId.ToString("N")[..12], journal.State, failure)); } catch { } }
    private static ModuleUpdateTransactionFailureCode Map(Exception exception) => exception switch
    { OperationCanceledException => ModuleUpdateTransactionFailureCode.Cancelled, UnauthorizedAccessException => ModuleUpdateTransactionFailureCode.Unauthorized, IOException => ModuleUpdateTransactionFailureCode.IoFailure, _ => ModuleUpdateTransactionFailureCode.Unexpected };
    private static ModuleUpdateTransactionFailureCode Map(Exception exception, ModuleUpdateTransactionState state) =>
        exception is IOException ? state switch
        {
            ModuleUpdateTransactionState.Preparing => ModuleUpdateTransactionFailureCode.CandidateCopyFailed,
            ModuleUpdateTransactionState.BackupStarted => ModuleUpdateTransactionFailureCode.BackupMoveFailed,
            ModuleUpdateTransactionState.CandidatePromotionStarted => ModuleUpdateTransactionFailureCode.PromotionFailed,
            _ => ModuleUpdateTransactionFailureCode.IoFailure
        } : Map(exception);
    private static ModuleUpdateTransactionResult Failure(Guid id, ModuleUpdateTransactionFailureCode code,
        ModuleUpdateTransactionState state = ModuleUpdateTransactionState.Failed) => new(false, id, state, code);
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static ModuleUpdateTransactionConfigurationException Configuration(ModuleUpdateTransactionConfigurationFailureCode code) => new(code, code.ToString());
    public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed, 1); return ValueTask.CompletedTask; }

    private sealed record InstalledSnapshot(string ModuleId, string Version, string Entry,
        SecureDirectoryIdentity DirectoryIdentity, IReadOnlyList<QmodStagedFile> Files);
    private sealed record JournalEnvelope(Guid TransactionId, string ModuleId);
    private sealed record LegacyLifecycleProgress(bool WindowsClosed, bool Deactivated, bool Unloaded,
        bool RuntimeRestored);
    private sealed record LegacyTransactionJournal(int SchemaVersion, Guid TransactionId,
        string EnvironmentIdentity, string ModuleId, string SourceVersion, string TargetVersion,
        string ModuleApiVersion, string PackageSha256, string VerifiedStagingIdentity,
        string UserModulesRootIdentity, ModuleUpdateTransactionState State,
        ModuleUpdateRuntimeState PreviousRuntimeState, LegacyLifecycleProgress LifecycleProgress, int Attempt,
        ModuleUpdateTransactionFailureCode LastFailureCode, bool HadInstalledModule,
        IReadOnlyList<QmodStagedFile> PayloadFiles, IReadOnlyList<QmodStagedFile> InstalledFiles,
        SecureDirectoryIdentity? InstalledDirectoryIdentity, SecureDirectoryIdentity? CandidateDirectoryIdentity,
        SecureDirectoryIdentity? BackupDirectoryIdentity, SecureDirectoryIdentity? PromotedDirectoryIdentity,
        DateTimeOffset StartedAtUtc, DateTimeOffset UpdatedAtUtc);
    internal sealed record LifecycleProgress(bool WindowsClosed, bool Deactivated, bool Unloaded,
        bool PromotedRuntimeRestoreStarted, bool PromotedRuntimeRestored,
        bool PromotedRuntimeQuiescedForRollback, bool PreviousRuntimeRestoreStarted,
        bool PreviousRuntimeRestoredAfterRollback);
    internal sealed record TransactionJournal(Guid TransactionId, string EnvironmentIdentity, string ModuleId,
        string SourceVersion, string TargetVersion, string ModuleApiVersion, string PackageSha256,
        string VerifiedStagingIdentity, string UserModulesRootIdentity, ModuleUpdateTransactionState State,
        ModuleUpdateRuntimeState PreviousRuntimeState, LifecycleProgress LifecycleProgress, int Attempt,
        ModuleUpdateTransactionFailureCode LastFailureCode, bool HadInstalledModule,
        IReadOnlyList<QmodStagedFile> PayloadFiles, IReadOnlyList<QmodStagedFile> InstalledFiles,
        SecureDirectoryIdentity? InstalledDirectoryIdentity, SecureDirectoryIdentity? CandidateDirectoryIdentity,
        SecureDirectoryIdentity? BackupDirectoryIdentity, SecureDirectoryIdentity? PromotedDirectoryIdentity,
        DateTimeOffset StartedAtUtc, DateTimeOffset UpdatedAtUtc)
    { public int SchemaVersion { get; init; } = JournalSchema; }
}
