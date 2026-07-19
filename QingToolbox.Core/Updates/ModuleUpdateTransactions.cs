using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

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
    ModuleIdentityMismatch, VersionNotNewer, ModuleApiIncompatible,
    RuntimeCloseFailed, DeactivateFailed, UnloadFailed, ModuleStillLoaded,
    CandidateCopyFailed, CandidateVerificationFailed, BackupMoveFailed,
    PromotionFailed, InstalledVerificationFailed, RuntimeRestoreFailed,
    RollbackFailed, RecoveryRequired, TransactionConflict, Cancelled,
    Unauthorized, IoFailure, Unexpected, UnsupportedEnvironment
}

public sealed record ModuleUpdateRuntimeState(bool HasWindows, bool IsActive, bool IsLoaded, bool HasStartupAuthorization);

public interface IModuleUpdateRuntimeCoordinator
{
    Task<ModuleUpdateRuntimeState> GetRuntimeStateAsync(string moduleId, CancellationToken cancellationToken);
    Task<bool> RequestCloseWindowsAsync(string moduleId, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(string moduleId, CancellationToken cancellationToken);
    Task<bool> UnloadAsync(string moduleId, CancellationToken cancellationToken);
    Task<bool> VerifyUnloadedAsync(string moduleId, CancellationToken cancellationToken);
    Task<bool> RestorePreviousRuntimeStateAsync(
        string moduleId, ModuleUpdateRuntimeState previousState, CancellationToken cancellationToken);
}

public sealed record ModuleUpdateTransactionInput(QmodVerifiedStagingAttestation VerifiedStaging);

public sealed record ModuleUpdateTransactionResult(
    bool Succeeded, Guid TransactionId, ModuleUpdateTransactionState State,
    ModuleUpdateTransactionFailureCode FailureCode, bool RolledBack = false, bool CleanupPending = false);

public sealed record ModuleUpdateRecoveryResult(
    int Recovered, int CleanupCompleted, int RecoveryRequired,
    IReadOnlyList<ModuleUpdateTransactionResult> Results);

public sealed record ModuleUpdateTransactionLogEvent(
    string EventName, string ModuleId, string SourceVersion, string TargetVersion,
    string TransactionIdPrefix, ModuleUpdateTransactionState State,
    ModuleUpdateTransactionFailureCode FailureCode = ModuleUpdateTransactionFailureCode.None);

internal sealed record ModuleUpdateTransactionTestHooks(
    Action<ModuleUpdateTransactionState>? StatePersisted = null,
    Action<string, string>? DirectoryMove = null,
    Action? CandidateCopyStarting = null,
    Action? InstalledVerificationStarting = null,
    Action? BackupCleanupStarting = null);

public sealed class ModuleUpdateTransactionService : IAsyncDisposable
{
    private const int JournalSchema = 1;
    private const string JournalName = "module-update-transaction.json";
    private const string OwnershipMarkerName = ".qing-transaction-owner";
    private readonly string _environmentIdentity;
    private readonly string _userModulesRoot;
    private readonly string _journalRoot;
    private readonly string _locksRoot;
    private readonly string _workRoot;
    private readonly string _physicalUserModulesRoot;
    private readonly string _moduleApiVersion;
    private readonly IModuleUpdateRuntimeCoordinator _runtime;
    private readonly Action<ModuleUpdateTransactionLogEvent>? _log;
    private readonly ModuleUpdateTransactionTestHooks? _hooks;
    private int _disposed;
    private static readonly JsonSerializerOptions JournalOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ModuleUpdateTransactionService(string environmentIdentity, string userModulesRoot,
        string transactionCacheRoot, string moduleApiVersion, IModuleUpdateRuntimeCoordinator runtime,
        Action<ModuleUpdateTransactionLogEvent>? log = null)
        : this(environmentIdentity, userModulesRoot, transactionCacheRoot, moduleApiVersion, runtime, log, null) { }

    internal ModuleUpdateTransactionService(string environmentIdentity, string userModulesRoot,
        string transactionCacheRoot, string moduleApiVersion, IModuleUpdateRuntimeCoordinator runtime,
        Action<ModuleUpdateTransactionLogEvent>? log, ModuleUpdateTransactionTestHooks? hooks)
    {
        if (environmentIdentity is not ("Development" or "ModuleTest"))
            throw new ArgumentException("B1 transaction execution is restricted to Development and ModuleTest.");
        ArgumentNullException.ThrowIfNull(runtime);
        _environmentIdentity = environmentIdentity;
        _userModulesRoot = ValidateAbsoluteNonRoot(userModulesRoot, nameof(userModulesRoot));
        var cache = ValidateAbsoluteNonRoot(transactionCacheRoot, nameof(transactionCacheRoot));
        _journalRoot = Path.Combine(cache, "Journal");
        _locksRoot = Path.Combine(cache, "Locks");
        _workRoot = Path.Combine(_userModulesRoot, ".qing-transactions");
        _moduleApiVersion = string.IsNullOrWhiteSpace(moduleApiVersion)
            ? throw new ArgumentException("Module API version is required.") : moduleApiVersion;
        _runtime = runtime; _log = log; _hooks = hooks;
        EnsureOwnedRoots();
        _physicalUserModulesRoot = PhysicalPath(_userModulesRoot);
    }

    public async Task<ModuleUpdateTransactionResult> ExecuteAsync(
        ModuleUpdateTransactionInput input, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(input);
        var attestation = input.VerifiedStaging ?? throw new ArgumentException("Verified staging is required.");
        var transactionId = Guid.NewGuid();
        if (!IsSafeModuleId(attestation.ModuleId) || attestation.EnvironmentIdentity != _environmentIdentity)
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.VerifiedStagingInvalid);
        if (attestation.ModuleApiVersion != _moduleApiVersion)
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.ModuleApiIncompatible);
        TransactionLease transactionLock;
        try { transactionLock = await AcquireLockAsync(attestation.ModuleId, cancellationToken); }
        catch (OperationCanceledException)
        {
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.Cancelled);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.Unauthorized);
        }
        catch (IOException)
        {
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.IoFailure);
        }
        catch
        {
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.Unexpected);
        }
        await using var heldTransactionLock = transactionLock;
        if (!ReattestVerifiedStaging(attestation))
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.VerifiedStagingInvalid);
        string? existing;
        try { existing = FindJournal(attestation.ModuleId); }
        catch (UnauthorizedAccessException)
        {
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.Unauthorized);
        }
        catch (IOException)
        {
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.IoFailure);
        }
        if (existing is not null)
            return Failure(transactionId, ModuleUpdateTransactionState.RecoveryRequired,
                ModuleUpdateTransactionFailureCode.RecoveryRequired);

        var installed = InstalledPath(attestation.ModuleId);
        var work = WorkPath(transactionId); var candidate = Path.Combine(work, "candidate");
        var backup = Path.Combine(work, "backup"); var failed = Path.Combine(work, "failed-candidate");
        InstalledIdentity? current = null;
        ModuleUpdateRuntimeState previousRuntime;
        TransactionJournal journal;
        try
        {
            Directory.CreateDirectory(work); WriteOwnerMarker(work, transactionId);
            previousRuntime = await _runtime.GetRuntimeStateAsync(attestation.ModuleId, cancellationToken);
            journal = new TransactionJournal(transactionId, _environmentIdentity, attestation.ModuleId,
                "", attestation.TargetVersion, attestation.ModuleApiVersion, attestation.PackageSha256,
                HashIdentity(attestation.PhysicalDirectoryIdentity), HashIdentity(_physicalUserModulesRoot),
                ModuleUpdateTransactionState.Preparing, previousRuntime, 1,
                ModuleUpdateTransactionFailureCode.None, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Persist(journal);
        }
        catch (OperationCanceledException)
        {
            TryDeleteUnstartedWork(work, transactionId);
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.Cancelled);
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteUnstartedWork(work, transactionId);
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.Unauthorized);
        }
        catch (IOException)
        {
            TryDeleteUnstartedWork(work, transactionId);
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.IoFailure);
        }
        catch
        {
            TryDeleteUnstartedWork(work, transactionId);
            return Failure(transactionId, ModuleUpdateTransactionState.Failed,
                ModuleUpdateTransactionFailureCode.Unexpected);
        }
        Log("Module update transaction started", journal);
        Log("Verified staging attested", journal);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(installed))
            {
                if (IsReparse(installed)) return await RejectAndCleanupAsync(journal,
                    ModuleUpdateTransactionFailureCode.InstalledManifestInvalid, work);
                current = ReadInstalledIdentity(installed);
                if (current is null) return await RejectAndCleanupAsync(journal,
                    ModuleUpdateTransactionFailureCode.InstalledManifestInvalid, work);
                journal = journal with { SourceVersion = current.Version, HadInstalledModule = true };
                if (current.ModuleId != attestation.ModuleId)
                    return await RejectAndCleanupAsync(journal, ModuleUpdateTransactionFailureCode.ModuleIdentityMismatch, work);
                if (SemanticVersion.Parse(attestation.TargetVersion).CompareTo(SemanticVersion.Parse(current.Version)) <= 0)
                    return await RejectAndCleanupAsync(journal, ModuleUpdateTransactionFailureCode.VersionNotNewer, work);
            }
            else if (File.Exists(installed))
                return await RejectAndCleanupAsync(journal, ModuleUpdateTransactionFailureCode.InstalledManifestInvalid, work);

            _hooks?.CandidateCopyStarting?.Invoke();
            BuildCandidate(attestation, candidate, transactionId);
            journal = Advance(journal, ModuleUpdateTransactionState.Prepared);
            Log("Candidate prepared", journal);

            journal = Advance(journal, ModuleUpdateTransactionState.Quiescing);
            Log("Runtime quiesce started", journal);
            if (previousRuntime.HasWindows && !await _runtime.RequestCloseWindowsAsync(attestation.ModuleId, cancellationToken))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.RuntimeCloseFailed, cancellationToken);
            if (previousRuntime.IsActive && !await _runtime.DeactivateAsync(attestation.ModuleId, cancellationToken))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.DeactivateFailed, cancellationToken);
            if (previousRuntime.IsLoaded && !await _runtime.UnloadAsync(attestation.ModuleId, cancellationToken))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.UnloadFailed, cancellationToken);
            if (!await _runtime.VerifyUnloadedAsync(attestation.ModuleId, cancellationToken))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.ModuleStillLoaded, cancellationToken);
            journal = Advance(journal, ModuleUpdateTransactionState.Quiesced);
            Log("Runtime quiesced", journal);

            if (journal.HadInstalledModule)
            {
                journal = Advance(journal, ModuleUpdateTransactionState.BackupStarted);
                Move(installed, backup);
                journal = Advance(journal, ModuleUpdateTransactionState.BackupCreated);
                Log("Backup created", journal);
            }
            journal = Advance(journal, ModuleUpdateTransactionState.CandidatePromotionStarted);
            Move(candidate, installed);
            journal = Advance(journal, ModuleUpdateTransactionState.CandidatePromoted);
            Log("Candidate promoted", journal);
            journal = Advance(journal, ModuleUpdateTransactionState.Verifying);
            _hooks?.InstalledVerificationStarting?.Invoke();
            if (!VerifyInstalled(attestation, installed, requireTransactionMarker: true))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.InstalledVerificationFailed, cancellationToken);
            journal = Advance(journal, ModuleUpdateTransactionState.Verified);
            Log("Installed module verified", journal);
            journal = Advance(journal, ModuleUpdateTransactionState.RuntimeRestoring);
            if (!await _runtime.RestorePreviousRuntimeStateAsync(attestation.ModuleId, previousRuntime, cancellationToken))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.RuntimeRestoreFailed, cancellationToken);
            Log("Runtime restored", journal);
            DeleteTransactionMarker(installed, transactionId);
            if (!VerifyInstalled(attestation, installed, requireTransactionMarker: false))
                return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                    ModuleUpdateTransactionFailureCode.InstalledVerificationFailed, cancellationToken);
            journal = Advance(journal, ModuleUpdateTransactionState.Committed);
            Log("Transaction committed", journal);
            try
            {
                _hooks?.BackupCleanupStarting?.Invoke();
                DeleteOwnedWork(work, transactionId);
                DeleteJournal(journal);
                return new(true, transactionId, ModuleUpdateTransactionState.Committed,
                    ModuleUpdateTransactionFailureCode.None);
            }
            catch
            {
                journal = Advance(journal, ModuleUpdateTransactionState.CleanupPending);
                Log("Cleanup pending", journal);
                return new(true, transactionId, ModuleUpdateTransactionState.CleanupPending,
                    ModuleUpdateTransactionFailureCode.None, CleanupPending: true);
            }
        }
        catch (OperationCanceledException)
        {
            return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                ModuleUpdateTransactionFailureCode.Cancelled, CancellationToken.None);
        }
        catch (UnauthorizedAccessException)
        {
            return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                ModuleUpdateTransactionFailureCode.Unauthorized, CancellationToken.None);
        }
        catch (IOException exception)
        {
            var code = journal.State switch
            {
                ModuleUpdateTransactionState.BackupStarted => ModuleUpdateTransactionFailureCode.BackupMoveFailed,
                ModuleUpdateTransactionState.CandidatePromotionStarted => ModuleUpdateTransactionFailureCode.PromotionFailed,
                ModuleUpdateTransactionState.Preparing => ModuleUpdateTransactionFailureCode.CandidateCopyFailed,
                _ => ModuleUpdateTransactionFailureCode.IoFailure
            };
            _ = exception;
            return await RollbackAsync(journal, installed, candidate, backup, failed, work, code, CancellationToken.None);
        }
        catch
        {
            return await RollbackAsync(journal, installed, candidate, backup, failed, work,
                ModuleUpdateTransactionFailureCode.Unexpected, CancellationToken.None);
        }
    }

    public async Task<ModuleUpdateRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var results = new List<ModuleUpdateTransactionResult>(); int recovered = 0, cleaned = 0, required = 0;
        foreach (var journalPath in Directory.EnumerateFiles(_journalRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransactionJournal journal;
            try { journal = ReadJournal(journalPath); }
            catch { required++; continue; }
            if (journal.EnvironmentIdentity != _environmentIdentity || !IsSafeModuleId(journal.ModuleId))
            { required++; continue; }
            if (!journal.UserModulesRootIdentity.Equals(HashIdentity(_physicalUserModulesRoot),
                    StringComparison.OrdinalIgnoreCase) ||
                journal.State == ModuleUpdateTransactionState.RecoveryRequired)
            { required++; continue; }
            await using var transactionLock = await AcquireLockAsync(journal.ModuleId, cancellationToken);
            Log("Recovery started", journal);
            var installed = InstalledPath(journal.ModuleId); var work = WorkPath(journal.TransactionId);
            var candidate = Path.Combine(work, "candidate"); var backup = Path.Combine(work, "backup");
            var failed = Path.Combine(work, "failed-candidate");
            try
            {
                if (journal.State is ModuleUpdateTransactionState.Committed or ModuleUpdateTransactionState.CleanupPending)
                {
                    DeleteOwnedWork(work, journal.TransactionId); DeleteJournal(journal); cleaned++;
                    results.Add(new(true, journal.TransactionId, ModuleUpdateTransactionState.Committed,
                        ModuleUpdateTransactionFailureCode.None));
                }
                else
                {
                    var result = await RollbackAsync(journal, installed, candidate, backup, failed, work,
                        ModuleUpdateTransactionFailureCode.RecoveryRequired, cancellationToken);
                    results.Add(result); if (result.RolledBack) recovered++; else required++;
                }
                Log("Recovery completed", journal);
            }
            catch
            {
                required++;
                results.Add(Failure(journal.TransactionId, ModuleUpdateTransactionState.RecoveryRequired,
                    ModuleUpdateTransactionFailureCode.RecoveryRequired));
            }
        }
        return new(recovered, cleaned, required, results);
    }

    private async Task<ModuleUpdateTransactionResult> RollbackAsync(TransactionJournal journal,
        string installed, string candidate, string backup, string failed, string work,
        ModuleUpdateTransactionFailureCode failure, CancellationToken cancellationToken)
    {
        try
        {
            var candidateWasPromoted = journal.State is ModuleUpdateTransactionState.CandidatePromoted or
                ModuleUpdateTransactionState.Verifying or ModuleUpdateTransactionState.Verified or
                ModuleUpdateTransactionState.RuntimeRestoring || HasTransactionMarker(installed, journal.TransactionId);
            journal = Advance(journal with { LastFailureCode = failure }, ModuleUpdateTransactionState.RollbackStarted);
            Log("Rollback started", journal, failure);
            if (Directory.Exists(installed) && candidateWasPromoted)
            {
                if (Directory.Exists(failed)) throw new IOException("Failed-candidate slot is occupied.");
                Move(installed, failed);
            }
            if (Directory.Exists(backup))
            {
                if (Directory.Exists(installed) || File.Exists(installed)) throw new IOException("Installed slot is occupied.");
                Move(backup, installed);
            }
            else if (!journal.HadInstalledModule && Directory.Exists(installed))
            {
                if (!HasTransactionMarker(installed, journal.TransactionId))
                    throw new IOException("Installed directory ownership could not be proven.");
                Directory.Delete(installed, true);
            }
            if (!await _runtime.RestorePreviousRuntimeStateAsync(
                    journal.ModuleId, journal.PreviousRuntimeState, cancellationToken))
                throw new IOException("Previous runtime state could not be restored.");
            journal = Advance(journal, ModuleUpdateTransactionState.RolledBack);
            Log("Rollback completed", journal, failure);
            DeleteOwnedWork(work, journal.TransactionId); DeleteJournal(journal);
            return new(false, journal.TransactionId, journal.State, failure, RolledBack: true);
        }
        catch
        {
            try { journal = Advance(journal with { LastFailureCode = failure }, ModuleUpdateTransactionState.RecoveryRequired); }
            catch { }
            return Failure(journal.TransactionId, ModuleUpdateTransactionState.RecoveryRequired,
                ModuleUpdateTransactionFailureCode.RollbackFailed);
        }
    }

    private async Task<ModuleUpdateTransactionResult> RejectAndCleanupAsync(TransactionJournal journal,
        ModuleUpdateTransactionFailureCode failure, string work)
    {
        journal = Advance(journal with { LastFailureCode = failure }, ModuleUpdateTransactionState.Failed);
        try { DeleteOwnedWork(work, journal.TransactionId); DeleteJournal(journal); } catch { }
        await Task.CompletedTask;
        Log("Transaction rejected", journal, failure);
        return Failure(journal.TransactionId, journal.State, failure);
    }

    private void BuildCandidate(QmodVerifiedStagingAttestation attestation, string candidate, Guid transactionId)
    {
        Directory.CreateDirectory(candidate); WriteOwnerMarker(Path.GetDirectoryName(candidate)!, transactionId);
        WriteOwnerMarker(candidate, transactionId);
        var payload = attestation.Files.Where(file => file.RelativePath is not
            (QmodPackageStagingService.StagingMetadataName or QmodPackageStagingService.PackageManifestName)).ToArray();
        foreach (var file in payload)
        {
            var source = SafeCombine(attestation.Directory, file.RelativePath);
            var destination = SafeCombine(candidate, file.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (IsReparse(source)) throw new IOException("Verified source changed.");
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.WriteThrough);
            using var hash = SHA256.Create();
            var actual = hash.ComputeHash(input); input.Position = 0; input.CopyTo(output); output.Flush(true);
            if (input.Length != file.Size || !Convert.ToHexString(actual).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Verified source changed during copy.");
        }
        if (!VerifyPayload(attestation, candidate, payload, OwnershipMarkerName))
            throw new IOException("Candidate verification failed.");
    }

    private bool ReattestVerifiedStaging(QmodVerifiedStagingAttestation attestation)
    {
        try
        {
            if (!Directory.Exists(attestation.Directory) || IsReparse(attestation.Directory) ||
                !IsSha256(attestation.OfficialReleaseIdentityHash) || !IsSha256(attestation.PackageSha256) ||
                !PhysicalPath(attestation.Directory).Equals(attestation.PhysicalDirectoryIdentity, StringComparison.OrdinalIgnoreCase) ||
                !IsWithin(attestation.PhysicalVerifiedRootIdentity, attestation.PhysicalDirectoryIdentity)) return false;
            return VerifyPayload(attestation, attestation.Directory, attestation.Files,
                QmodPackageStagingService.StagingMetadataName);
        }
        catch { return false; }
    }

    private bool VerifyInstalled(QmodVerifiedStagingAttestation attestation, string installed,
        bool requireTransactionMarker) =>
        VerifyPayload(attestation, installed, attestation.Files.Where(file => file.RelativePath is not
            (QmodPackageStagingService.StagingMetadataName or QmodPackageStagingService.PackageManifestName)).ToArray(),
            requireTransactionMarker ? new[] { OwnershipMarkerName } : []);

    private static bool VerifyPayload(QmodVerifiedStagingAttestation attestation, string root,
        IReadOnlyList<QmodStagedFile> expected, params string[] allowedUnhashedFiles)
    {
        try
        {
            if (!Directory.Exists(root) || IsReparse(root)) return false;
            var expectedMap = expected.ToDictionary(file => file.RelativePath.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            var allowedExtras = new HashSet<string>(allowedUnhashedFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                if (IsReparse(directory)) return false;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (IsReparse(path)) return false;
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!expectedMap.Remove(relative, out var record))
                {
                    if (allowedExtras.Remove(relative)) continue;
                    return false;
                }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (stream.Length != record.Size || hash != record.Sha256.ToLowerInvariant()) return false;
            }
            if (expectedMap.Count != 0 || allowedExtras.Count != 0) return false;
            var identity = ReadInstalledIdentity(root);
            return identity is not null && identity.ModuleId == attestation.ModuleId &&
                   identity.Version == attestation.TargetVersion && File.Exists(SafeCombine(root, identity.Entry));
        }
        catch { return false; }
    }

    private static InstalledIdentity? ReadInstalledIdentity(string root)
    {
        try
        {
            var path = Path.Combine(root, "module.json"); if (!File.Exists(path) || IsReparse(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path)); var element = document.RootElement;
            var id = element.GetProperty("id").GetString(); var version = element.GetProperty("version").GetString();
            var entry = element.GetProperty("entry").GetString();
            if (!IsSafeModuleId(id) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(entry) ||
                Path.IsPathRooted(entry) || entry.Contains("..", StringComparison.Ordinal)) return null;
            _ = SemanticVersion.Parse(version);
            return new(id!, version!, entry.Replace('/', Path.DirectorySeparatorChar));
        }
        catch { return null; }
    }

    private TransactionJournal Advance(TransactionJournal journal, ModuleUpdateTransactionState state)
    {
        var updated = journal with { State = state, UpdatedAtUtc = DateTimeOffset.UtcNow };
        Persist(updated); _hooks?.StatePersisted?.Invoke(state); return updated;
    }

    private void Persist(TransactionJournal journal)
    {
        var final = JournalPath(journal.TransactionId); var temp = final + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalOptions);
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { stream.Write(bytes); stream.Flush(true); }
        if (File.Exists(final)) File.Move(temp, final, true); else File.Move(temp, final);
    }

    private TransactionJournal ReadJournal(string path)
    {
        if (IsReparse(path) || !Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(
                Path.GetFullPath(_journalRoot), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unsafe journal path.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        { "schemaVersion", "transactionId", "environmentIdentity", "moduleId", "sourceVersion", "targetVersion",
          "moduleApiVersion", "packageSha256", "verifiedStagingIdentity", "userModulesRootIdentity", "state",
          "previousRuntimeState", "attempt", "lastFailureCode", "hadInstalledModule", "startedAtUtc", "updatedAtUtc" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new JsonException();
        if (!seen.SetEquals(allowed)) throw new JsonException();
        var runtime = document.RootElement.GetProperty("previousRuntimeState");
        var runtimeAllowed = new HashSet<string>(StringComparer.Ordinal)
            { "hasWindows", "isActive", "isLoaded", "hasStartupAuthorization" };
        var runtimeSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in runtime.EnumerateObject())
            if (!runtimeAllowed.Contains(property.Name) || !runtimeSeen.Add(property.Name)) throw new JsonException();
        if (!runtimeSeen.SetEquals(runtimeAllowed)) throw new JsonException();
        var journal = JsonSerializer.Deserialize<TransactionJournal>(document.RootElement, JournalOptions)
            ?? throw new JsonException();
        if (journal.SchemaVersion != JournalSchema || journal.TransactionId == Guid.Empty ||
            !IsSafeModuleId(journal.ModuleId) || !Enum.IsDefined(journal.State) ||
            !Enum.IsDefined(journal.LastFailureCode) || journal.Attempt < 1 ||
            journal.PackageSha256.Length != 64 || journal.VerifiedStagingIdentity.Length != 64 ||
            journal.UserModulesRootIdentity.Length != 64) throw new JsonException();
        return journal;
    }

    private string? FindJournal(string moduleId)
    {
        foreach (var path in Directory.EnumerateFiles(_journalRoot, "*.json"))
        { try { if (ReadJournal(path).ModuleId == moduleId) return path; } catch { return path; } }
        return null;
    }

    private void DeleteJournal(TransactionJournal journal)
    { var path = JournalPath(journal.TransactionId); if (File.Exists(path)) File.Delete(path); }

    private async Task<TransactionLease> AcquireLockAsync(string moduleId, CancellationToken token)
    {
        var identity = HashIdentity($"{_physicalUserModulesRoot.ToUpperInvariant()}\0{_environmentIdentity}\0{moduleId}");
        var path = Path.Combine(_locksRoot, identity + ".transaction.lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                stream.SetLength(0); var marker = Encoding.ASCII.GetBytes(Environment.ProcessId.ToString());
                stream.Write(marker); stream.Flush(true); return new(stream);
            }
            catch (IOException) { await Task.Delay(40, token); }
        }
    }

    private void Move(string source, string destination)
    {
        if (!Directory.Exists(source) || IsReparse(source) || Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("Unsafe transaction move.");
        var physicalSource = PhysicalPath(source);
        var physicalDestinationParent = PhysicalPath(Path.GetDirectoryName(destination)!);
        if (!IsWithin(_physicalUserModulesRoot, physicalSource) ||
            !IsWithin(_physicalUserModulesRoot, physicalDestinationParent))
            throw new IOException("Transaction move escaped the physical user modules root.");
        _hooks?.DirectoryMove?.Invoke(source, destination);
        if (_hooks?.DirectoryMove is null) Directory.Move(source, destination);
    }

    private void EnsureOwnedRoots()
    {
        foreach (var path in new[] { _userModulesRoot, _journalRoot, _locksRoot, _workRoot })
        {
            Directory.CreateDirectory(path);
            EnsureNoReparseAncestors(path);
        }
        if (!Path.GetPathRoot(_userModulesRoot)!.Equals(Path.GetPathRoot(_workRoot), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Transaction work must share the module volume.");
    }

    private string InstalledPath(string moduleId) => SafeCombine(_userModulesRoot, moduleId);
    private string WorkPath(Guid transactionId) => SafeCombine(_workRoot, transactionId.ToString("N"));
    private string JournalPath(Guid transactionId) => Path.Combine(_journalRoot, transactionId.ToString("N") + ".json");
    private static string SafeCombine(string root, string relative)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Path escaped its transaction root.");
        return path;
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateAbsoluteNonRoot(string path, string name)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Path must be absolute.", name);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!)) throw new ArgumentException("Volume roots are forbidden.", name);
        return full;
    }

    private static bool IsSafeModuleId(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value is not ("." or "..") && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => char.IsAsciiHexDigit(character));
    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void EnsureNoReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (current is not null)
        {
            if (Directory.Exists(current) && IsReparse(current))
                throw new IOException("Transaction roots cannot contain reparse points.");
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }
    private static string HashIdentity(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string PhysicalPath(string path)
    {
        using var handle = NativeMethods.OpenDirectory(path);
        if (handle.IsInvalid || NativeMethods.IsReparse(handle)) throw new IOException("Directory attestation failed.");
        return NativeMethods.FinalPath(handle);
    }

    private static void WriteOwnerMarker(string work, Guid transactionId)
    {
        var marker = Path.Combine(work, OwnershipMarkerName);
        if (File.Exists(marker)) return;
        File.WriteAllText(marker, transactionId.ToString("D"), new UTF8Encoding(false));
    }

    private static bool HasTransactionMarker(string directory, Guid transactionId)
    {
        try
        {
            var marker = Path.Combine(directory, OwnershipMarkerName);
            return Directory.Exists(directory) && !IsReparse(directory) && File.Exists(marker) &&
                   !IsReparse(marker) && File.ReadAllText(marker).Trim().Equals(
                       transactionId.ToString("D"), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void DeleteTransactionMarker(string directory, Guid transactionId)
    {
        if (!HasTransactionMarker(directory, transactionId))
            throw new IOException("Installed transaction ownership could not be proven.");
        File.Delete(Path.Combine(directory, OwnershipMarkerName));
    }

    private static void DeleteOwnedWork(string work, Guid transactionId)
    {
        if (!Directory.Exists(work)) return;
        var marker = Path.Combine(work, OwnershipMarkerName);
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != transactionId.ToString("D"))
            throw new IOException("Transaction ownership could not be proven.");
        if (IsReparse(work)) throw new IOException("Transaction work was replaced.");
        Directory.Delete(work, true);
    }

    private static void TryDeleteUnstartedWork(string work, Guid transactionId)
    {
        try { DeleteOwnedWork(work, transactionId); } catch { }
    }

    private void Log(string name, TransactionJournal journal,
        ModuleUpdateTransactionFailureCode failure = ModuleUpdateTransactionFailureCode.None)
    {
        try { _log?.Invoke(new(name, journal.ModuleId, journal.SourceVersion, journal.TargetVersion,
            journal.TransactionId.ToString("N")[..12], journal.State, failure)); } catch { }
    }

    private static ModuleUpdateTransactionResult Failure(Guid id, ModuleUpdateTransactionState state,
        ModuleUpdateTransactionFailureCode code) => new(false, id, state, code);

    public ValueTask DisposeAsync() { Interlocked.Exchange(ref _disposed, 1); return ValueTask.CompletedTask; }

    private sealed record InstalledIdentity(string ModuleId, string Version, string Entry);
    internal sealed record TransactionJournal(
        Guid TransactionId, string EnvironmentIdentity, string ModuleId, string SourceVersion,
        string TargetVersion, string ModuleApiVersion, string PackageSha256,
        string VerifiedStagingIdentity, string UserModulesRootIdentity,
        ModuleUpdateTransactionState State, ModuleUpdateRuntimeState PreviousRuntimeState,
        int Attempt, ModuleUpdateTransactionFailureCode LastFailureCode, bool HadInstalledModule,
        DateTimeOffset StartedAtUtc, DateTimeOffset UpdatedAtUtc)
    { public int SchemaVersion { get; init; } = JournalSchema; }

    private sealed class TransactionLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { try { stream.SetLength(0); stream.Flush(true); } catch { } stream.Dispose(); return ValueTask.CompletedTask; }
    }

    private static class NativeMethods
    {
        private const uint ReadAttributes = 0x80, ShareRead = 1, ShareWrite = 2, OpenExisting = 3;
        private const uint BackupSemantics = 0x02000000, OpenReparse = 0x00200000;
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
        public static SafeFileHandle OpenDirectory(string path) => CreateFile(path, ReadAttributes, ShareRead | ShareWrite,
            IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse, IntPtr.Zero);
        public static bool IsReparse(SafeFileHandle handle) => (File.GetAttributes(FinalPath(handle)) & FileAttributes.ReparsePoint) != 0;
        public static string FinalPath(SafeFileHandle handle)
        {
            var builder = new StringBuilder(1024); var length = GetFinalPathNameByHandle(handle, builder, 1024, 0);
            if (length == 0 || length >= 1024) throw new IOException("Physical path query failed.");
            var value = builder.ToString(); if (value.StartsWith("\\\\?\\")) value = value[4..];
            return Path.GetFullPath(value);
        }
    }
}
