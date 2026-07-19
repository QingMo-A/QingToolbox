using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    BackupMovedBeforeJournal, CandidateMovedBeforeJournal, RuntimeRestoredBeforeCommit,
    CommittedBeforeCleanup
}

internal sealed record ModuleUpdateTransactionTestHooks(
    Action<ModuleUpdateTransactionState>? StatePersisted = null,
    Action<string, string>? DirectoryMove = null,
    Action? CandidateCopyStarting = null,
    Action? InstalledVerificationStarting = null,
    Action? BackupCleanupStarting = null,
    Action<ModuleUpdateTransactionCrashPoint>? CrashPoint = null,
    Action? MarkerDeleteStarting = null,
    Action? WorkDeleteStarting = null,
    Action? JournalDeleteStarting = null,
    Action<ModuleUpdateTransactionState>? JournalPersistStarting = null);

public sealed class ModuleUpdateTransactionService : IAsyncDisposable
{
    private const int JournalSchema = 2;
    private const string OwnershipMarkerName = ".qing-transaction-owner";
    private readonly string _environmentIdentity;
    private readonly string _userModulesRoot;
    private readonly string _journalRoot;
    private readonly string _locksRoot;
    private readonly string _workRoot;
    private readonly string _physicalUserModulesRoot;
    private readonly string _physicalCacheRoot;
    private readonly string _moduleApiVersion;
    private readonly IModuleUpdateRuntimeCoordinator _runtime;
    private readonly Action<ModuleUpdateTransactionLogEvent>? _log;
    private readonly ModuleUpdateTransactionTestHooks? _hooks;
    private int _disposed;
    private static readonly JsonSerializerOptions JournalOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ModuleUpdateTransactionService(string environmentIdentity, string userModulesRoot,
        string transactionCacheRoot, string moduleApiVersion, IModuleUpdateRuntimeCoordinator runtime,
        Action<ModuleUpdateTransactionLogEvent>? log = null)
        : this(environmentIdentity, userModulesRoot, transactionCacheRoot, moduleApiVersion, runtime, log, null) { }

    internal ModuleUpdateTransactionService(string environmentIdentity, string userModulesRoot,
        string transactionCacheRoot, string moduleApiVersion, IModuleUpdateRuntimeCoordinator runtime,
        Action<ModuleUpdateTransactionLogEvent>? log, ModuleUpdateTransactionTestHooks? hooks)
    {
        if (environmentIdentity is not ("Development" or "ModuleTest"))
            throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.UnsupportedEnvironment);
        ArgumentNullException.ThrowIfNull(runtime);
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
        _moduleApiVersion = moduleApiVersion; _runtime = runtime; _log = log; _hooks = hooks;
        try
        {
            foreach (var root in new[] { _userModulesRoot, cache, _journalRoot, _locksRoot, _workRoot })
                SecureWindowsFileSystem.CreateOrdinaryDirectoryTree(root);
            _physicalUserModulesRoot = SecureWindowsFileSystem.PhysicalDirectory(_userModulesRoot);
            _physicalCacheRoot = SecureWindowsFileSystem.PhysicalDirectory(cache);
            if (SecureWindowsFileSystem.IsWithin(_physicalUserModulesRoot, _physicalCacheRoot) ||
                SecureWindowsFileSystem.IsWithin(_physicalCacheRoot, _physicalUserModulesRoot))
                throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.OverlappingRoots);
            if (!Path.GetPathRoot(_physicalUserModulesRoot)!.Equals(
                    Path.GetPathRoot(SecureWindowsFileSystem.PhysicalDirectory(_workRoot)), StringComparison.OrdinalIgnoreCase))
                throw Configuration(ModuleUpdateTransactionConfigurationFailureCode.InvalidUserModulesRoot);
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
        if (!ReattestVerifiedStaging(attestation)) return Failure(transactionId,
            ModuleUpdateTransactionFailureCode.VerifiedStagingInvalid);
        if (ModuleJournalEntries(attestation.ModuleId).Any()) return Failure(transactionId,
            ModuleUpdateTransactionFailureCode.RecoveryRequired, ModuleUpdateTransactionState.RecoveryRequired);

        var installed = InstalledPath(attestation.ModuleId); var work = WorkPath(transactionId);
        var candidate = Path.Combine(work, "candidate"); var backup = Path.Combine(work, "backup");
        var failed = Path.Combine(work, "failed-candidate");
        var payload = Payload(attestation); TransactionJournal journal; bool commitPersisted = false;
        try
        {
            Directory.CreateDirectory(work); SecureWindowsFileSystem.WriteOwnerMarker(work, OwnershipMarkerName, transactionId);
            var previousRuntime = await _runtime.GetRuntimeStateAsync(attestation.ModuleId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            journal = new(transactionId, _environmentIdentity, attestation.ModuleId, "", attestation.TargetVersion,
                attestation.ModuleApiVersion, attestation.PackageSha256,
                SecureWindowsFileSystem.HashIdentity(attestation.PhysicalDirectoryIdentity),
                SecureWindowsFileSystem.HashIdentity(_physicalUserModulesRoot), ModuleUpdateTransactionState.Preparing,
                previousRuntime, new(false, false, false, false), 1, ModuleUpdateTransactionFailureCode.None,
                false, payload, now, now);
            Persist(journal); Log("Module update transaction started", journal); Log("Verified staging attested", journal);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            TryDeleteWork(work, transactionId); return Failure(transactionId, Map(exception));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(installed))
            {
                var current = ReadInstalledIdentity(installed);
                if (current is null) return await RejectAsync(journal, work,
                    ModuleUpdateTransactionFailureCode.InstalledManifestInvalid);
                journal = journal with { SourceVersion = current.Version, HadInstalledModule = true };
                if (current.ModuleId != attestation.ModuleId) return await RejectAsync(journal, work,
                    ModuleUpdateTransactionFailureCode.ModuleIdentityMismatch);
                if (SemanticVersion.Parse(attestation.TargetVersion).CompareTo(SemanticVersion.Parse(current.Version)) <= 0)
                    return await RejectAsync(journal, work, ModuleUpdateTransactionFailureCode.VersionNotNewer);
            }
            else if (File.Exists(installed)) return await RejectAsync(journal, work,
                ModuleUpdateTransactionFailureCode.InstalledManifestInvalid);

            _hooks?.CandidateCopyStarting?.Invoke(); BuildCandidate(attestation, candidate, transactionId, payload);
            journal = Advance(journal, ModuleUpdateTransactionState.Prepared);
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
                journal = Advance(journal, ModuleUpdateTransactionState.BackupStarted);
                Move(installed, backup); _hooks?.CrashPoint?.Invoke(ModuleUpdateTransactionCrashPoint.BackupMovedBeforeJournal);
                journal = Advance(journal, ModuleUpdateTransactionState.BackupCreated);
            }
            journal = Advance(journal, ModuleUpdateTransactionState.CandidatePromotionStarted);
            Move(candidate, installed); _hooks?.CrashPoint?.Invoke(ModuleUpdateTransactionCrashPoint.CandidateMovedBeforeJournal);
            journal = Advance(journal, ModuleUpdateTransactionState.CandidatePromoted);
            journal = Advance(journal, ModuleUpdateTransactionState.Verifying);
            _hooks?.InstalledVerificationStarting?.Invoke();
            if (!ValidateInstalledCandidate(installed, payload, journal.ModuleId, journal.TargetVersion, transactionId))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.InstalledVerificationFailed, cancellationToken);
            journal = Advance(journal, ModuleUpdateTransactionState.Verified);
            journal = Advance(journal, ModuleUpdateTransactionState.RuntimeRestoring);
            if (!await _runtime.RestorePreviousRuntimeStateAsync(journal.ModuleId, journal.PreviousRuntimeState, cancellationToken))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.RuntimeRestoreFailed, cancellationToken);
            journal = UpdateProgress(journal, journal.LifecycleProgress with { RuntimeRestored = true });
            if (!ValidateInstalledCandidate(installed, payload, journal.ModuleId, journal.TargetVersion, transactionId))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.InstalledVerificationFailed, cancellationToken);
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
            if (commitPersisted) return PostCommitPending(journal);
            return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                Map(exception, journal.State), CancellationToken.None);
        }
    }

    public async Task<ModuleUpdateRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var results = new List<ModuleUpdateTransactionResult>(); int recovered = 0, cleaned = 0, required = 0;
        foreach (var namespaceDirectory in Directory.EnumerateDirectories(_journalRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SecureWindowsFileSystem.IsReparsePath(namespaceDirectory)) { required++; continue; }
            foreach (var path in Directory.EnumerateFiles(namespaceDirectory, "*.json", SearchOption.TopDirectoryOnly).ToArray())
            {
                TransactionJournal journal;
                try { journal = ReadJournal(path, namespaceDirectory); }
                catch { required++; continue; }
                CrashRecoverableFileLock transactionLock;
                try { transactionLock = await AcquireTransactionLockAsync(journal.ModuleId, cancellationToken); }
                catch { required++; continue; }
                await using var held = transactionLock;
                var installed = InstalledPath(journal.ModuleId); var work = WorkPath(journal.TransactionId);
                var candidate = Path.Combine(work, "candidate"); var backup = Path.Combine(work, "backup");
                var failed = Path.Combine(work, "failed-candidate");
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
        if (!ValidateInstalledCandidate(installed, journal.PayloadFiles, journal.ModuleId, journal.TargetVersion,
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
                if (!await _runtime.RestorePreviousRuntimeStateAsync(journal.ModuleId,
                        journal.PreviousRuntimeState, token)) return RequireRecovery(journal);
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
                !ValidateInstalledCandidate(installed, journal.PayloadFiles, journal.ModuleId, journal.TargetVersion, journal.TransactionId))
                return RequireRecovery(journal);
            return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                ModuleUpdateTransactionFailureCode.RecoveryRequired, token);
        }
        return RequireRecovery(journal);
    }

    private async Task<ModuleUpdateTransactionResult> RollbackAsync(TransactionJournal journal, string installed,
        string candidate, string backup, string failed, string work, ModuleUpdateTransactionFailureCode failure,
        CancellationToken cancellationToken)
    {
        try
        {
            journal = Advance(journal with { LastFailureCode = failure }, ModuleUpdateTransactionState.RollbackStarted);
            if (Directory.Exists(installed) && Directory.Exists(backup))
            {
                if (!ValidateInstalledCandidate(installed, journal.PayloadFiles, journal.ModuleId, journal.TargetVersion,
                        journal.TransactionId)) return RequireRecovery(journal);
                if (Directory.Exists(failed) || File.Exists(failed)) return RequireRecovery(journal);
                Move(installed, failed);
            }
            else if (Directory.Exists(installed) && !journal.HadInstalledModule)
            {
                if (!ValidateInstalledCandidate(installed, journal.PayloadFiles, journal.ModuleId, journal.TargetVersion,
                        journal.TransactionId)) return RequireRecovery(journal);
                Move(installed, failed);
            }
            if (Directory.Exists(backup))
            {
                if (Directory.Exists(installed) || File.Exists(installed)) return RequireRecovery(journal);
                Move(backup, installed);
            }
            if (!await _runtime.RestorePreviousRuntimeStateAsync(journal.ModuleId, journal.PreviousRuntimeState,
                    cancellationToken)) return RequireRecovery(journal);
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
        try
        {
            return SecureWindowsFileSystem.IsWithin(_physicalUserModulesRoot,
                       SecureWindowsFileSystem.PhysicalDirectory(installed)) &&
                   ValidatePayloadTree(installed, expected, moduleId, version, marker);
        }
        catch { return false; }
    }

    private static bool ValidatePayloadTree(string root, IReadOnlyList<QmodStagedFile> expected, string moduleId,
        string version, Guid? marker, bool requirePackageManifest = false)
    {
        try
        {
            var physicalRoot = SecureWindowsFileSystem.PhysicalDirectory(root);
            var expectedMap = new Dictionary<string, QmodStagedFile>(StringComparer.Ordinal);
            var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
            var collision = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in expected)
            {
                var relative = NormalizeRelative(file.RelativePath);
                if (!expectedMap.TryAdd(relative, file) || !collision.Add(CollisionKey(relative)) || !IsSha256(file.Sha256)) return false;
                var parent = Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar));
                while (!string.IsNullOrEmpty(parent))
                {
                    expectedDirectories.Add(parent.Replace('\\', '/')); parent = Path.GetDirectoryName(parent);
                }
            }
            if (marker is not null) collision.Add(CollisionKey(OwnershipMarkerName));
            var foundFiles = new HashSet<string>(StringComparer.Ordinal); var foundDirs = new HashSet<string>(StringComparer.Ordinal);
            byte[]? manifestBytes = null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                if (SecureWindowsFileSystem.IsReparsePath(entry)) return false;
                var relative = NormalizeRelative(Path.GetRelativePath(root, entry));
                if (!collision.Contains(CollisionKey(relative)) && !collision.Add(CollisionKey(relative))) return false;
                if (Directory.Exists(entry)) { if (!expectedDirectories.Contains(relative)) return false; foundDirs.Add(relative); continue; }
                if (marker is not null && relative == OwnershipMarkerName)
                {
                    if (!SecureWindowsFileSystem.HasOwnerMarker(root, OwnershipMarkerName, marker.Value)) return false;
                    foundFiles.Add(relative); continue;
                }
                if (!expectedMap.TryGetValue(relative, out var record)) return false;
                using var handle = SecureWindowsFileSystem.OpenStableRead(entry, physicalRoot);
                var length = RandomAccess.GetLength(handle); if (length != record.Size) return false;
                using var stream = new FileStream(handle, FileAccess.Read, 65536, false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                MemoryStream? manifest = relative == "module.json" ? new() : null; var buffer = new byte[65536]; long readTotal = 0;
                while (true)
                {
                    var read = stream.Read(buffer); if (read == 0) break; readTotal += read;
                    hash.AppendData(buffer, 0, read); manifest?.Write(buffer, 0, read);
                }
                if (readTotal != length || RandomAccess.GetLength(handle) != length ||
                    !Convert.ToHexString(hash.GetHashAndReset()).Equals(record.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
                if (manifest is not null) manifestBytes = manifest.ToArray();
                foundFiles.Add(relative);
            }
            if (!foundDirs.SetEquals(expectedDirectories) || expectedMap.Keys.Any(key => !foundFiles.Contains(key)) ||
                (marker is not null && !foundFiles.Contains(OwnershipMarkerName))) return false;
            if (requirePackageManifest && !expectedMap.ContainsKey(QmodPackageStagingService.PackageManifestName)) return false;
            return ValidateManifest(manifestBytes, moduleId, version, expectedMap);
        }
        catch { return false; }
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
        if (string.IsNullOrWhiteSpace(normalized) || normalized.StartsWith('/') || normalized.Split('/').Any(x => x is "" or "." or ".."))
            throw new IOException("Unsafe relative path.");
        return normalized;
    }
    private static string CollisionKey(string relative) => relative.Normalize(NormalizationForm.FormC).ToUpperInvariant();
    private static IReadOnlyList<QmodStagedFile> Payload(QmodVerifiedStagingAttestation attestation) =>
        attestation.Files.Where(file => file.RelativePath is not QmodPackageStagingService.PackageManifestName).ToArray();

    private static InstalledIdentity? ReadInstalledIdentity(string installed)
    {
        try
        {
            var path = Path.Combine(installed, "module.json");
            if (!File.Exists(path) || SecureWindowsFileSystem.IsReparsePath(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path)); var root = document.RootElement;
            var id = root.GetProperty("id").GetString(); var version = root.GetProperty("version").GetString();
            var entry = root.GetProperty("entry").GetString();
            if (SecureModuleIdentity.Validate(id) != ModuleIdentityFailure.None ||
                !SemanticVersion.TryParse(version!, out _) || string.IsNullOrWhiteSpace(entry) ||
                !File.Exists(SecureWindowsFileSystem.SafeCombine(installed, entry))) return null;
            return new(id!, version!);
        }
        catch { return null; }
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

    private void Move(string source, string destination)
    {
        if (!Directory.Exists(source) || SecureWindowsFileSystem.IsReparsePath(source) ||
            Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Unsafe move.");
        if (!SecureWindowsFileSystem.IsWithin(_physicalUserModulesRoot, SecureWindowsFileSystem.PhysicalDirectory(source)) ||
            !SecureWindowsFileSystem.IsWithin(_physicalUserModulesRoot,
                SecureWindowsFileSystem.PhysicalDirectory(Path.GetDirectoryName(destination)!))) throw new IOException("Move escaped root.");
        _hooks?.DirectoryMove?.Invoke(source, destination); if (_hooks?.DirectoryMove is null) Directory.Move(source, destination);
    }

    private TransactionJournal UpdateProgress(TransactionJournal journal, LifecycleProgress progress)
    { var updated = journal with { LifecycleProgress = progress, UpdatedAtUtc = DateTimeOffset.UtcNow }; Persist(updated); return updated; }
    private TransactionJournal Advance(TransactionJournal journal, ModuleUpdateTransactionState state)
    {
        var updated = journal with { State = state, UpdatedAtUtc = DateTimeOffset.UtcNow };
        Persist(updated); _hooks?.StatePersisted?.Invoke(state); return updated;
    }

    private void Persist(TransactionJournal journal)
    {
        _hooks?.JournalPersistStarting?.Invoke(journal.State);
        var directory = JournalNamespace(journal.ModuleId); SecureWindowsFileSystem.CreateOrdinaryDirectoryTree(directory);
        var final = JournalPath(journal.ModuleId, journal.TransactionId);
        var temp = final + ".tmp-" + Guid.NewGuid().ToString("N"); var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalOptions);
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            File.Move(temp, final, true);
        }
        finally { if (File.Exists(temp)) try { File.Delete(temp); } catch { } }
    }

    private TransactionJournal ReadJournal(string path, string namespaceDirectory)
    {
        if (SecureWindowsFileSystem.IsReparsePath(path) || !Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(
                Path.GetFullPath(namespaceDirectory), StringComparison.OrdinalIgnoreCase)) throw new JsonException();
        var bytes = File.ReadAllBytes(path); if (bytes.Length == 0 || (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)) throw new JsonException();
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
        ValidateObject(document.RootElement,
        ["schemaVersion", "transactionId", "environmentIdentity", "moduleId", "sourceVersion", "targetVersion",
         "moduleApiVersion", "packageSha256", "verifiedStagingIdentity", "userModulesRootIdentity", "state",
         "previousRuntimeState", "lifecycleProgress", "attempt", "lastFailureCode", "hadInstalledModule",
         "payloadFiles", "startedAtUtc", "updatedAtUtc"]);
        ValidateObject(document.RootElement.GetProperty("previousRuntimeState"),
            ["hasWindows", "isActive", "isLoaded", "hasStartupAuthorization"]);
        ValidateObject(document.RootElement.GetProperty("lifecycleProgress"),
            ["windowsClosed", "deactivated", "unloaded", "runtimeRestored"]);
        foreach (var file in document.RootElement.GetProperty("payloadFiles").EnumerateArray())
            ValidateObject(file, ["relativePath", "size", "sha256"]);
        var journal = JsonSerializer.Deserialize<TransactionJournal>(document.RootElement, JournalOptions) ?? throw new JsonException();
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
            journal.StartedAtUtc == default || journal.UpdatedAtUtc < journal.StartedAtUtc || journal.PayloadFiles.Count == 0)
            throw new JsonException();
        foreach (var file in journal.PayloadFiles)
            if (file.Size < 0 || !IsSha256(file.Sha256) || NormalizeRelative(file.RelativePath) != file.RelativePath.Replace('\\', '/'))
                throw new JsonException();
        return journal;
    }

    private static void ValidateObject(JsonElement element, IReadOnlyCollection<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException();
        var allowed = new HashSet<string>(names, StringComparer.Ordinal); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new JsonException();
        if (!seen.SetEquals(allowed)) throw new JsonException();
    }

    private IEnumerable<string> ModuleJournalEntries(string moduleId)
    {
        var directory = JournalNamespace(moduleId); if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFileSystemEntries(directory).ToArray();
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
        return await SecureWindowsFileSystem.AcquireLockAsync(Path.Combine(_locksRoot, identity + ".transaction.lock"), token);
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
        if (File.Exists(path)) File.Delete(path);
        var directory = JournalNamespace(journal.ModuleId); if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
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

    private sealed record InstalledIdentity(string ModuleId, string Version);
    internal sealed record LifecycleProgress(bool WindowsClosed, bool Deactivated, bool Unloaded, bool RuntimeRestored);
    internal sealed record TransactionJournal(Guid TransactionId, string EnvironmentIdentity, string ModuleId,
        string SourceVersion, string TargetVersion, string ModuleApiVersion, string PackageSha256,
        string VerifiedStagingIdentity, string UserModulesRootIdentity, ModuleUpdateTransactionState State,
        ModuleUpdateRuntimeState PreviousRuntimeState, LifecycleProgress LifecycleProgress, int Attempt,
        ModuleUpdateTransactionFailureCode LastFailureCode, bool HadInstalledModule,
        IReadOnlyList<QmodStagedFile> PayloadFiles, DateTimeOffset StartedAtUtc, DateTimeOffset UpdatedAtUtc)
    { public int SchemaVersion { get; init; } = JournalSchema; }
}
