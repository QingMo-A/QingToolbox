using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("QingToolbox.DevTools.ModulePackageStagingSmokeTest")]
[assembly: InternalsVisibleTo("QingToolbox.DevTools.ModuleUpdateTransactionSmokeTest")]

namespace QingToolbox.Core.Updates;

public sealed record QmodStagingLimits(
    int MaximumEntries = 2048,
    long MaximumSingleFileBytes = 128L * 1024 * 1024,
    long MaximumTotalUncompressedBytes = 256L * 1024 * 1024,
    int MaximumDirectoryDepth = 16,
    int MaximumRelativePathLength = 240,
    double MaximumEntryCompressionRatio = 200,
    double MaximumOverallCompressionRatio = 100,
    int MaximumManifestBytes = 64 * 1024)
{
    public void Validate()
    {
        if (MaximumEntries <= 0 || MaximumSingleFileBytes <= 0 || MaximumTotalUncompressedBytes <= 0 ||
            MaximumDirectoryDepth <= 0 || MaximumRelativePathLength <= 0 || MaximumEntryCompressionRatio < 1 ||
            MaximumOverallCompressionRatio < 1 || MaximumManifestBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(QmodStagingLimits));
    }
}

public sealed record QmodStagingInput(
    VerifiedModulePackage VerifiedPackage,
    ModulePackageDownloadIdentity ReleaseIdentity,
    string ModuleApiVersion,
    string SourceIdentity);

public readonly record struct QmodStagingOperationKey(
    string PackagePath, string ModuleId, string TargetVersion, string FileName,
    long ExpectedSize, string Sha256, string ModuleApiVersion, string SourceIdentity,
    string EnvironmentIdentity, string OfficialReleaseIdentityHash);

public readonly record struct QmodModuleVersionKey(
    string StagingRootIdentity, string EnvironmentIdentity, string ModuleId, string TargetVersion);

internal sealed record QmodStagingTestHooks(
    Func<QmodStagingInput, CancellationToken, Task>? PublicationLockAcquired = null,
    Func<QmodStagingInput, CancellationToken, Task>? ExtractionCapacityAcquired = null,
    Func<QmodStagingInput, string, CancellationToken, Task>? CandidateAttestationStarting = null,
    Action<string, string>? PublicationMove = null,
    Action? CallerCancellationObserved = null,
    Func<QmodStagingInput, Task>? PublicationMoveCompleted = null,
    Action? PublicationLockMarkerCleanup = null);

public enum QmodStagingConfigurationFailureCode
{
    UnsafeStagingRoot,
    UnsafeUserModulesRoot,
    OverlappingRoots,
    UnsupportedEnvironment
}

public sealed class QmodStagingConfigurationException(
    QmodStagingConfigurationFailureCode failureCode, string message) : ArgumentException(message)
{
    public QmodStagingConfigurationFailureCode FailureCode { get; } = failureCode;
}

public enum QmodStagingFailureCode
{
    None, PackageMissing, PackageChanged, PackageHashMismatch, PackageSizeMismatch,
    InvalidArchive, UnsafeEntryPath, PathCollision, UnsupportedEntryType,
    EntryLimitExceeded, SingleFileLimitExceeded, TotalSizeLimitExceeded,
    CompressionRatioExceeded, ManifestMissing, ManifestDuplicate, ManifestInvalid,
    ModuleIdentityMismatch, VersionMismatch, ModuleApiIncompatible, StagingConflict,
    StagingMetadataInvalid, VerifiedStagingInvalid,
    Cancelled, IoFailure, Unauthorized, Unexpected
}

public sealed record QmodStagedFile(string RelativePath, long Size, string Sha256);

public sealed class QmodVerifiedStagingAttestation
{
    internal QmodVerifiedStagingAttestation(string directory, string physicalDirectoryIdentity,
        string physicalVerifiedRootIdentity, string officialReleaseIdentityHash,
        string moduleId, string targetVersion, string moduleApiVersion, string packageSha256,
        Guid transactionId, string environmentIdentity, IReadOnlyList<QmodStagedFile> files,
        QmodStagedFile stagingMetadataFile)
    {
        Directory = directory; PhysicalDirectoryIdentity = physicalDirectoryIdentity;
        PhysicalVerifiedRootIdentity = physicalVerifiedRootIdentity;
        OfficialReleaseIdentityHash = officialReleaseIdentityHash; ModuleId = moduleId;
        TargetVersion = targetVersion; ModuleApiVersion = moduleApiVersion; PackageSha256 = packageSha256;
        TransactionId = transactionId; EnvironmentIdentity = environmentIdentity;
        Files = Array.AsReadOnly(files.ToArray());
        StagingMetadataFile = stagingMetadataFile;
    }
    public string Directory { get; }
    public string PhysicalDirectoryIdentity { get; }
    public string PhysicalVerifiedRootIdentity { get; }
    public string OfficialReleaseIdentityHash { get; }
    public string ModuleId { get; }
    public string TargetVersion { get; }
    public string ModuleApiVersion { get; }
    public string PackageSha256 { get; }
    public Guid TransactionId { get; }
    public string EnvironmentIdentity { get; }
    public IReadOnlyList<QmodStagedFile> Files { get; }
    public QmodStagedFile StagingMetadataFile { get; }
}

public interface IQmodVerifiedStagingAttestor
{
    string EnvironmentIdentity { get; }
    string PhysicalVerifiedRootIdentity { get; }
    Task<QmodVerifiedStagingAttestation?> ReattestAsync(
        QmodVerifiedStagingAttestation attestation, CancellationToken cancellationToken);
}

public sealed record QmodStagingResult(
    bool Succeeded,
    bool Reused,
    QmodStagingFailureCode FailureCode,
    string? StagingDirectory = null,
    int FileCount = 0,
    long TotalUncompressedBytes = 0)
{
    public static QmodStagingResult Failure(QmodStagingFailureCode code) => new(false, false, code);
}

public sealed record QmodStagingLogEvent(
    string EventName, string ModuleId, string Version, string PackageHashPrefix,
    QmodStagingFailureCode FailureCode = QmodStagingFailureCode.None,
    int EntryCount = 0, long TotalUncompressedBytes = 0);

public sealed class QmodPackageStagingService : IAsyncDisposable, IQmodVerifiedStagingAttestor
{
    public const string PackageManifestName = "qmod.json";
    public const string StagingMetadataName = "qmod-staging.json";
    private readonly string _stagingRoot;
    private readonly string _incomingRoot;
    private readonly string _verifiedRoot;
    private readonly string _locksRoot;
    private readonly string? _userModulesRoot;
    private readonly string _environmentIdentity;
    private readonly string _physicalVerifiedRoot;
    private readonly SecureDirectoryIdentity _verifiedRootIdentity;
    private readonly string _physicalLocksRoot;
    private readonly SecureDirectoryIdentity _locksRootIdentity;
    private readonly QmodStagingLimits _limits;
    private readonly TimeProvider _timeProvider;
    private readonly Action<QmodStagingLogEvent>? _log;
    private readonly SemaphoreSlim _parallelism;
    private readonly QmodStagingTestHooks? _testHooks;
    private readonly ConcurrentDictionary<QmodStagingOperationKey, StagingOperation> _inflight =
        new(QmodStagingOperationKeyComparer.Instance);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifecycleSync = new();
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposeState;

    public QmodPackageStagingService(string stagingRoot, TimeProvider timeProvider, string environmentIdentity,
        string? userModulesRoot = null, QmodStagingLimits? limits = null,
        Action<QmodStagingLogEvent>? log = null, int maximumParallelism = 2)
        : this(stagingRoot, timeProvider, environmentIdentity, userModulesRoot, limits, log, maximumParallelism, null)
    { }

    internal QmodPackageStagingService(string stagingRoot, TimeProvider timeProvider, string environmentIdentity,
        string? userModulesRoot, QmodStagingLimits? limits, Action<QmodStagingLogEvent>? log,
        int maximumParallelism, QmodStagingTestHooks? testHooks)
    {
        if (!Path.IsPathFullyQualified(stagingRoot)) throw new QmodStagingConfigurationException(
            QmodStagingConfigurationFailureCode.UnsafeStagingRoot, "Staging root must be absolute.");
        if (maximumParallelism <= 0) throw new ArgumentOutOfRangeException(nameof(maximumParallelism));
        _stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        if (Path.GetPathRoot(_stagingRoot) == _stagingRoot) throw new QmodStagingConfigurationException(
            QmodStagingConfigurationFailureCode.UnsafeStagingRoot, "Staging root cannot be a volume root.");
        _incomingRoot = Path.Combine(_stagingRoot, "Incoming");
        _verifiedRoot = Path.Combine(_stagingRoot, "Verified");
        _locksRoot = Path.Combine(_stagingRoot, "Locks");
        _environmentIdentity = environmentIdentity is "Production" or "Development" or "ModuleTest"
            ? environmentIdentity : throw new QmodStagingConfigurationException(
                QmodStagingConfigurationFailureCode.UnsupportedEnvironment, "Unsupported execution environment.");
        if (!string.IsNullOrWhiteSpace(userModulesRoot) && !Path.IsPathFullyQualified(userModulesRoot))
            throw new QmodStagingConfigurationException(QmodStagingConfigurationFailureCode.UnsafeUserModulesRoot,
                "User modules root must be absolute.");
        _userModulesRoot = string.IsNullOrWhiteSpace(userModulesRoot)
            ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(userModulesRoot));
        if (_userModulesRoot is not null && Path.GetPathRoot(_userModulesRoot) == _userModulesRoot)
            throw new QmodStagingConfigurationException(QmodStagingConfigurationFailureCode.UnsafeUserModulesRoot,
                "User modules root cannot be a volume root.");
        ValidateRootIsolation();
        _timeProvider = timeProvider;
        _limits = limits ?? new();
        _limits.Validate();
        _log = log;
        _parallelism = new(maximumParallelism, maximumParallelism);
        _testHooks = testHooks;
        EnsureRootChain();
        _physicalVerifiedRoot = SecureWindowsFileSystem.PhysicalDirectory(_verifiedRoot);
        _verifiedRootIdentity = SecureWindowsFileSystem.DirectoryIdentity(_verifiedRoot);
        _physicalLocksRoot = SecureWindowsFileSystem.PhysicalDirectory(_locksRoot);
        _locksRootIdentity = SecureWindowsFileSystem.DirectoryIdentity(_locksRoot);
    }

    public string EnvironmentIdentity => _environmentIdentity;
    public string PhysicalVerifiedRootIdentity => _physicalVerifiedRoot;

    public Task<QmodVerifiedStagingAttestation?> ReattestAsync(
        QmodVerifiedStagingAttestation attestation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (attestation.EnvironmentIdentity != _environmentIdentity ||
                !attestation.PhysicalVerifiedRootIdentity.Equals(_physicalVerifiedRoot, StringComparison.OrdinalIgnoreCase) ||
                !SecureWindowsFileSystem.PhysicalDirectory(_verifiedRoot).Equals(_physicalVerifiedRoot, StringComparison.OrdinalIgnoreCase) ||
                SecureWindowsFileSystem.DirectoryIdentity(_verifiedRoot) != _verifiedRootIdentity ||
                !SecureWindowsFileSystem.IsWithin(_physicalVerifiedRoot,
                    SecureWindowsFileSystem.PhysicalDirectory(attestation.Directory)) ||
                !SecureWindowsFileSystem.PhysicalDirectory(attestation.Directory).Equals(
                    attestation.PhysicalDirectoryIdentity, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<QmodVerifiedStagingAttestation?>(null);
            return Task.FromResult<QmodVerifiedStagingAttestation?>(attestation);
        }
        catch
        {
            return Task.FromResult<QmodVerifiedStagingAttestation?>(null);
        }
    }

    public Task<QmodStagingResult> StageAsync(QmodStagingInput input, CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        QmodStagingOperationKey key;
        try { key = ValidateStaticInputAndCreateKey(input); }
        catch (StagingException exception) { return Task.FromResult(Reject(exception.Code, input)); }
        StagingOperation? candidate = null;
        StagingOperation operation;
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposeState != 0, this);
            candidate = new(CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token),
                () => RunAndRemoveAsync(key, input, candidate!));
            operation = _inflight.GetOrAdd(key, candidate);
        }
        if (!ReferenceEquals(operation, candidate))
        {
            candidate.Dispose();
            Log("Shared operation joined", input);
        }
        return callerToken.CanBeCanceled ? AwaitWithCommitSemanticsAsync(operation, callerToken) : operation.Task;
    }

    private async Task<QmodStagingResult> AwaitWithCommitSemanticsAsync(
        StagingOperation operation, CancellationToken callerToken)
    {
        try { return await operation.Task.WaitAsync(callerToken); }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            _testHooks?.CallerCancellationObserved?.Invoke();
            if (await operation.ObserveCommitAtCancellationBoundaryAsync()) return await operation.Task;
            throw;
        }
    }

    public void Cancel(QmodStagingInput input)
    {
        lock (_lifecycleSync) { if (_disposeState != 0) return; }
        try
        {
            var key = ValidateStaticInputAndCreateKey(input);
            if (_inflight.TryGetValue(key, out var operation)) operation.Cancellation.Cancel();
        }
        catch (Exception exception) when (exception is StagingException or ObjectDisposedException) { }
    }

    public async Task<QmodVerifiedStagingAttestation?> AttestVerifiedStagingAsync(
        QmodStagingInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        _ = ValidateStaticInputAndCreateKey(input);
        lock (_lifecycleSync) ObjectDisposedException.ThrowIf(_disposeState != 0, this);
        EnsureRootChain();
        await using var packageStream = OpenAndAttestPackage(Path.GetFullPath(input.VerifiedPackage.FilePath));
        if (packageStream.Length != input.ReleaseIdentity.ExpectedSize) return null;
        var hash = await SHA256.HashDataAsync(packageStream, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(input.ReleaseIdentity.Sha256))) return null;
        packageStream.Position = 0;
        await using var publicationLock = await AcquirePublicationLockAsync(input, cancellationToken);
        var final = BuildFinalPath(input);
        if (!Directory.Exists(final)) return null;
        var result = await ValidateExistingStagingAsync(final, input, packageStream, cancellationToken);
        if (!result.Succeeded) return null;
        var metadata = await ReadAndValidateStagingMetadataAsync(
            Path.Combine(final, StagingMetadataName), input, cancellationToken);
        var metadataPath = Path.Combine(final, StagingMetadataName);
        using var metadataHandle = SecureWindowsFileSystem.OpenStableRead(metadataPath, GetPhysicalDirectoryPath(final));
        await using var metadataStream = new FileStream(metadataHandle, FileAccess.Read, 65536, false);
        var metadataLength = metadataStream.Length;
        var metadataHash = await SHA256.HashDataAsync(metadataStream, cancellationToken);
        if (metadataStream.Length != metadataLength) return null;
        var metadataFile = new QmodStagedFile(StagingMetadataName, metadataLength,
            Convert.ToHexString(metadataHash).ToLowerInvariant());
        return new(final, GetPhysicalDirectoryPath(final), GetPhysicalDirectoryPath(_verifiedRoot),
            HashOfficialReleaseIdentity(input.ReleaseIdentity.OfficialUrl), input.ReleaseIdentity.ModuleId,
            input.ReleaseIdentity.TargetVersion, input.ModuleApiVersion,
            input.ReleaseIdentity.Sha256.ToLowerInvariant(), metadata.TransactionId,
            _environmentIdentity, metadata.Files.ToArray(), metadataFile);
    }

    private async Task<QmodStagingResult> RunAndRemoveAsync(QmodStagingOperationKey key, QmodStagingInput input, StagingOperation operation)
    {
        try { return await StageCoreAsync(input, operation, operation.Cancellation.Token); }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<QmodStagingOperationKey, StagingOperation>(key, operation));
            operation.Dispose();
        }
    }

    private async Task<QmodStagingResult> StageCoreAsync(
        QmodStagingInput input, StagingOperation operation, CancellationToken token)
    {
        string? partial = null;
        Log("Staging started", input);
        try
        {
            var packagePath = Path.GetFullPath(input.VerifiedPackage.FilePath);
            EnsureRootChain();
            await using var packageStream = OpenAndAttestPackage(packagePath);
            Log("Package source path attested", input);
            if (packageStream.Length != input.ReleaseIdentity.ExpectedSize)
                return Reject(QmodStagingFailureCode.PackageSizeMismatch, input);
            var packageHash = await SHA256.HashDataAsync(packageStream, token);
            if (!CryptographicOperations.FixedTimeEquals(packageHash, Convert.FromHexString(input.ReleaseIdentity.Sha256)))
                return Reject(QmodStagingFailureCode.PackageHashMismatch, input);
            packageStream.Position = 0;

            Log("Publication lock waiting", input);
            await using var publicationLock = await AcquirePublicationLockAsync(input, token);
            Log("Publication lock acquired", input);
            if (_testHooks?.PublicationLockAcquired is { } lockHook) await lockHook(input, token);
            await _parallelism.WaitAsync(token);
            try
            {
                Log("Extraction capacity acquired", input);
                if (_testHooks?.ExtractionCapacityAcquired is { } capacityHook) await capacityHook(input, token);
                var final = BuildFinalPath(input);
                if (Directory.Exists(final))
                {
                    Log("Existing staging validation started", input);
                    var reuse = await ValidateExistingStagingAsync(final, input, packageStream, token);
                    if (reuse.Succeeded)
                    {
                        Log("Existing staging reused", input, reuse.FileCount, reuse.TotalUncompressedBytes);
                        return reuse;
                    }
                    Log(reuse.FailureCode == QmodStagingFailureCode.StagingMetadataInvalid
                        ? "Existing staging metadata rejected" : "Existing staging tree rejected", input, failure: reuse.FailureCode);
                    return Reject(reuse.FailureCode, input);
                }
                var versionRoot = Path.GetDirectoryName(final)!;
                if (HasConflictingVersionContent(versionRoot, final))
                {
                    Log("Conflicting SHA rejected", input, failure: QmodStagingFailureCode.StagingConflict);
                    return Reject(QmodStagingFailureCode.StagingConflict, input);
                }

                partial = Path.Combine(_incomingRoot, $"{Guid.NewGuid():N}.partial");
                CreateSafeDirectoryChain(_incomingRoot, partial);
                EnsureSafeParents(_incomingRoot, partial);
                using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
                var entries = InspectArchive(archive);
                Log("Archive metadata accepted", input, entries.Files.Count, entries.TotalLength);
                var packageManifest = await ReadPackageManifestAsync(entries.PackageManifest, token);
                var moduleManifest = await ReadModuleManifestAsync(entries.ModuleManifest, token);
                ValidateManifests(packageManifest, moduleManifest, input, entries);
                Log("Manifest validation accepted", input, entries.Files.Count, entries.TotalLength);

                var files = new List<QmodStagedFile>(entries.Files.Count);
                long actualTotal = 0;
                foreach (var entry in entries.Files.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested();
                    var target = ResolveTarget(partial, entry.RelativePath);
                    CreateSafeDirectoryChain(partial, Path.GetDirectoryName(target)!);
                    EnsureSafeParents(partial, Path.GetDirectoryName(target)!);
                    await using var source = entry.Entry.Open();
                    await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var written = await CopyBoundedAsync(source, destination, hash, entry.Entry.Length,
                        _limits.MaximumSingleFileBytes, _limits.MaximumTotalUncompressedBytes - actualTotal, token);
                    await destination.FlushAsync(token);
                    destination.Flush(true);
                    actualTotal = checked(actualTotal + written);
                    files.Add(new(entry.RelativePath, written, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                }
                if (actualTotal != entries.TotalLength) throw new StagingException(QmodStagingFailureCode.InvalidArchive);
                Log("Extraction completed", input, files.Count, actualTotal);
                var transactionId = Guid.NewGuid();
                await WriteMetadataAsync(partial, input, files, actualTotal, transactionId, token);
                if (_testHooks?.CandidateAttestationStarting is { } candidateStartingHook)
                    await candidateStartingHook(input, partial, token);
                packageStream.Position = 0;
                var candidate = await AttestStagingDirectoryAsync(partial, _incomingRoot, input, packageStream,
                    token, transactionId, reused: false);
                if (!candidate.Succeeded) return Reject(candidate.FailureCode, input);
                CreateSafeDirectoryChain(_verifiedRoot, versionRoot);
                EnsureSafeParents(_verifiedRoot, versionRoot);
                token.ThrowIfCancellationRequested();
                if (HasConflictingVersionContent(versionRoot, final))
                    return Reject(QmodStagingFailureCode.StagingConflict, input);
                try
                {
                    operation.CommitPublication(() =>
                    {
                        if (_testHooks?.PublicationMove is { } moveHook) moveHook(partial, final);
                        else Directory.Move(partial, final);
                    });
                }
                catch (IOException) when (Directory.Exists(final) || HasConflictingVersionContent(versionRoot, final))
                {
                    return Reject(QmodStagingFailureCode.StagingConflict, input);
                }
                partial = null;
                Log("Publication move completed", input, files.Count, actualTotal);
                if (_testHooks?.PublicationMoveCompleted is { } committedHook)
                {
                    try { await committedHook(input); }
                    catch { Log("Post-commit diagnostic failed", input, failure: QmodStagingFailureCode.IoFailure); }
                }
                Log("Verified directory published", input, files.Count, actualTotal);
                return new(true, false, QmodStagingFailureCode.None, final, files.Count, actualTotal);
            }
            finally { _parallelism.Release(); }
        }
        catch (OperationCanceledException)
        {
            Log("Staging cancelled", input, failure: QmodStagingFailureCode.Cancelled);
            return QmodStagingResult.Failure(QmodStagingFailureCode.Cancelled);
        }
        catch (StagingException exception) { return Reject(exception.Code, input); }
        catch (InvalidDataException) { return Reject(QmodStagingFailureCode.InvalidArchive, input); }
        catch (JsonException) { return Reject(QmodStagingFailureCode.ManifestInvalid, input); }
        catch (UnauthorizedAccessException) { return Reject(QmodStagingFailureCode.Unauthorized, input); }
        catch (IOException) { return Reject(QmodStagingFailureCode.IoFailure, input); }
        catch (Exception) { return Reject(QmodStagingFailureCode.Unexpected, input); }
        finally
        {
            if (!string.IsNullOrEmpty(partial) && Directory.Exists(partial))
            {
                try { DeletePartialTree(partial); }
                catch { Log("Partial cleanup failed", input, failure: QmodStagingFailureCode.IoFailure); }
            }
        }
    }

    private ArchiveInspection InspectArchive(ZipArchive archive)
    {
        if (archive.Entries.Count == 0) throw new StagingException(QmodStagingFailureCode.InvalidArchive);
        if (archive.Entries.Count > _limits.MaximumEntries) throw new StagingException(QmodStagingFailureCode.EntryLimitExceeded);
        var seen = new Dictionary<string, EntryKind>(StringComparer.OrdinalIgnoreCase);
        var files = new List<InspectedEntry>();
        InspectedEntry? packageManifest = null, moduleManifest = null;
        long total = 0, compressed = 0;
        foreach (var entry in archive.Entries)
        {
            var inspected = InspectEntry(entry);
            if (!inspected.IsDirectory && inspected.RelativePath.Equals(PackageManifestName, StringComparison.OrdinalIgnoreCase) && packageManifest is not null)
                throw new StagingException(QmodStagingFailureCode.ManifestDuplicate);
            if (!inspected.IsDirectory && inspected.RelativePath.Equals("module.json", StringComparison.OrdinalIgnoreCase) && moduleManifest is not null)
                throw new StagingException(QmodStagingFailureCode.ManifestDuplicate);
            RegisterPath(seen, inspected.RelativePath, inspected.IsDirectory);
            if (inspected.IsDirectory) continue;
            if (entry.Length > _limits.MaximumSingleFileBytes) throw new StagingException(QmodStagingFailureCode.SingleFileLimitExceeded);
            total = checked(total + entry.Length);
            compressed = checked(compressed + entry.CompressedLength);
            if (total > _limits.MaximumTotalUncompressedBytes) throw new StagingException(QmodStagingFailureCode.TotalSizeLimitExceeded);
            if (entry.Length > 0 && (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > _limits.MaximumEntryCompressionRatio))
                throw new StagingException(QmodStagingFailureCode.CompressionRatioExceeded);
            files.Add(inspected);
            if (inspected.RelativePath.Equals(PackageManifestName, StringComparison.OrdinalIgnoreCase))
            {
                packageManifest = inspected;
            }
            if (inspected.RelativePath.Equals("module.json", StringComparison.OrdinalIgnoreCase))
            {
                moduleManifest = inspected;
            }
        }
        if (total > 0 && (compressed == 0 || total / (double)compressed > _limits.MaximumOverallCompressionRatio))
            throw new StagingException(QmodStagingFailureCode.CompressionRatioExceeded);
        if (packageManifest is null || moduleManifest is null) throw new StagingException(QmodStagingFailureCode.ManifestMissing);
        if (files.Count <= 2) throw new StagingException(QmodStagingFailureCode.InvalidArchive);
        return new(files, packageManifest, moduleManifest, total);
    }

    private InspectedEntry InspectEntry(ZipArchiveEntry entry)
    {
        var raw = entry.FullName;
        if (string.IsNullOrEmpty(raw) || raw.IndexOf('\0') >= 0 || raw[0] is '/' or '\\' || raw.StartsWith("//") || raw.StartsWith("\\\\"))
            throw new StagingException(QmodStagingFailureCode.UnsafeEntryPath);
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        var unixType = unixMode & 0xF000;
        if ((windowsAttributes & FileAttributes.ReparsePoint) != 0 || unixType is 0xA000 or 0x1000 or 0x2000 or 0x6000 or 0xC000)
            throw new StagingException(QmodStagingFailureCode.UnsupportedEntryType);
        var isDirectory = raw.EndsWith('/') || raw.EndsWith('\\') || (windowsAttributes & FileAttributes.Directory) != 0 || unixType == 0x4000;
        if (unixType != 0 && unixType != 0x8000 && unixType != 0x4000)
            throw new StagingException(QmodStagingFailureCode.UnsupportedEntryType);
        if (isDirectory && entry.Length != 0) throw new StagingException(QmodStagingFailureCode.UnsupportedEntryType);
        var segments = raw.Replace('\\', '/').Split('/', StringSplitOptions.None);
        if (segments[^1].Length == 0 && isDirectory) segments = segments[..^1];
        if (segments.Length == 0 || segments.Length > _limits.MaximumDirectoryDepth || segments.Any(segment => !IsSafeSegment(segment)))
            throw new StagingException(QmodStagingFailureCode.UnsafeEntryPath);
        var normalized = string.Join('/', segments.Select(segment => segment.Normalize(NormalizationForm.FormC)));
        if (normalized.Length > _limits.MaximumRelativePathLength) throw new StagingException(QmodStagingFailureCode.UnsafeEntryPath);
        return new(entry, normalized, isDirectory);
    }

    private static bool IsSafeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.Contains(':') || segment.Any(ch => ch < 32 || Path.GetInvalidFileNameChars().Contains(ch))) return false;
        var stem = segment.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)) return false;
        return !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
            stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9');
    }

    private static void RegisterPath(Dictionary<string, EntryKind> seen, string path, bool directory)
    {
        var segments = path.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var parent = string.Join('/', segments[..i]);
            if (seen.TryGetValue(parent, out var kind) && kind == EntryKind.File)
                throw new StagingException(QmodStagingFailureCode.PathCollision);
            seen.TryAdd(parent, EntryKind.Directory);
        }
        var requested = directory ? EntryKind.Directory : EntryKind.File;
        if (seen.TryGetValue(path, out var existing))
        {
            if (existing != requested || !directory) throw new StagingException(QmodStagingFailureCode.PathCollision);
            return;
        }
        seen.Add(path, requested);
    }

    private async Task<PackageManifest> ReadPackageManifestAsync(InspectedEntry entry, CancellationToken token)
    {
        var bytes = await ReadBoundedAsync(entry.Entry, _limits.MaximumManifestBytes, token);
        using var document = ParseStrictJson(bytes, new HashSet<string>(StringComparer.Ordinal)
        { "schemaVersion", "moduleId", "version", "moduleApiVersion", "entryManifest" });
        var root = document.RootElement;
        var schema = RequiredInt(root, "schemaVersion");
        if (schema != 1) throw new StagingException(QmodStagingFailureCode.ManifestInvalid);
        return new(schema, RequiredText(root, "moduleId"), RequiredText(root, "version"),
            RequiredText(root, "moduleApiVersion"), RequiredText(root, "entryManifest"));
    }

    private async Task<ModuleIdentityManifest> ReadModuleManifestAsync(InspectedEntry entry, CancellationToken token)
    {
        var bytes = await ReadBoundedAsync(entry.Entry, _limits.MaximumManifestBytes, token);
        using var document = ParseStrictJson(bytes, null);
        return new(RequiredText(document.RootElement, "id"), RequiredText(document.RootElement, "version"),
            RequiredText(document.RootElement, "entry"));
    }

    private static JsonDocument ParseStrictJson(byte[] bytes, IReadOnlySet<string>? allowedProperties)
    {
        if (bytes.Length == 0 || bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            throw new StagingException(QmodStagingFailureCode.ManifestInvalid);
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw new StagingException(QmodStagingFailureCode.ManifestInvalid); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name) || allowedProperties is not null && !allowedProperties.Contains(property.Name))
            { document.Dispose(); throw new StagingException(QmodStagingFailureCode.ManifestInvalid); }
        ValidateNoDuplicateProperties(document.RootElement);
        return document;
    }

    private static void ValidateNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new StagingException(QmodStagingFailureCode.ManifestInvalid);
                ValidateNoDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateNoDuplicateProperties(item);
    }

    private static void ValidateManifests(PackageManifest package, ModuleIdentityManifest module,
        QmodStagingInput input, ArchiveInspection entries)
    {
        if (!package.EntryManifest.Equals("module.json", StringComparison.Ordinal)) throw new StagingException(QmodStagingFailureCode.ManifestInvalid);
        if (!package.ModuleId.Equals(input.ReleaseIdentity.ModuleId, StringComparison.Ordinal) || !module.Id.Equals(package.ModuleId, StringComparison.Ordinal))
            throw new StagingException(QmodStagingFailureCode.ModuleIdentityMismatch);
        if (!package.Version.Equals(input.ReleaseIdentity.TargetVersion, StringComparison.Ordinal) || !module.Version.Equals(package.Version, StringComparison.Ordinal))
            throw new StagingException(QmodStagingFailureCode.VersionMismatch);
        if (!package.ModuleApiVersion.Equals(input.ModuleApiVersion, StringComparison.Ordinal) ||
            !package.ModuleApiVersion.Equals(ModuleUpdateIdentity.ModuleApiVersion, StringComparison.Ordinal))
            throw new StagingException(QmodStagingFailureCode.ModuleApiIncompatible);
        var normalizedEntry = module.Entry.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (!entries.Files.Any(file => file.RelativePath.Equals(normalizedEntry, StringComparison.OrdinalIgnoreCase)) ||
            !normalizedEntry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new StagingException(QmodStagingFailureCode.ManifestInvalid);
    }

    private QmodStagingOperationKey ValidateStaticInputAndCreateKey(QmodStagingInput input)
    {
        var package = input.VerifiedPackage;
        var identity = input.ReleaseIdentity;
        if (!IsSafeModuleId(identity.ModuleId) || !SemanticVersion.TryParse(identity.TargetVersion, out _) ||
            identity.TargetVersion.Length > 128 || identity.ExpectedSize <= 0 || identity.Sha256.Length != 64 ||
            !identity.Sha256.All(Uri.IsHexDigit) || !IsSafeFileName(identity.FileName) ||
            input.SourceIdentity != "qingtoolbox-official" ||
            input.ModuleApiVersion != ModuleUpdateIdentity.ModuleApiVersion)
            throw new StagingException(QmodStagingFailureCode.PackageChanged);
        try { _ = Convert.FromHexString(identity.Sha256); }
        catch (FormatException) { throw new StagingException(QmodStagingFailureCode.PackageChanged); }
        if (package.ModuleId != identity.ModuleId || package.Version.ToString() != identity.TargetVersion ||
            package.FileName != identity.FileName || package.Size != identity.ExpectedSize ||
            !package.Sha256.Equals(identity.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new StagingException(QmodStagingFailureCode.PackageChanged);
        try
        {
            OfficialModulePackageTransport.ValidatePackage(new(identity.FileName, new(identity.OfficialUrl), identity.ExpectedSize, identity.Sha256));
        }
        catch (ModulePackageTransportException) { throw new StagingException(QmodStagingFailureCode.PackageChanged); }
        var releaseIdentityHash = HashOfficialReleaseIdentity(identity.OfficialUrl);
        string packagePath;
        try
        {
            packagePath = Path.GetFullPath(package.FilePath);
            if (!Path.IsPathFullyQualified(package.FilePath) ||
                !package.FilePath.Equals(packagePath, StringComparison.OrdinalIgnoreCase) ||
                IsWithin(_stagingRoot, packagePath) || _userModulesRoot is not null && IsWithin(_userModulesRoot, packagePath))
                throw new StagingException(QmodStagingFailureCode.PackageChanged);
            if (File.Exists(packagePath) && IsReparse(packagePath))
                throw new StagingException(QmodStagingFailureCode.PackageChanged);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new StagingException(QmodStagingFailureCode.PackageChanged); }
        // LocalVersion is UI/update-selection context; it does not alter the selected Release asset trust identity.
        return new(packagePath, identity.ModuleId, identity.TargetVersion, identity.FileName,
            identity.ExpectedSize, identity.Sha256.ToLowerInvariant(), input.ModuleApiVersion,
            input.SourceIdentity, _environmentIdentity, releaseIdentityHash);
    }

    private static string HashOfficialReleaseIdentity(string officialUrl)
    {
        var uri = new Uri(officialUrl, UriKind.Absolute);
        var builder = new UriBuilder(uri) { Host = uri.IdnHost.ToLowerInvariant(), Port = -1, Fragment = string.Empty };
        var canonical = builder.Uri.AbsoluteUri;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private string BuildFinalPath(QmodStagingInput input)
    {
        var identity = input.ReleaseIdentity;
        var path = Path.GetFullPath(Path.Combine(_verifiedRoot, identity.ModuleId, identity.TargetVersion, identity.Sha256.ToLowerInvariant()));
        EnsureWithin(_verifiedRoot, path);
        return path;
    }

    private async Task<QmodStagingResult> ValidateExistingStagingAsync(
        string final, QmodStagingInput input, Stream packageStream, CancellationToken token)
        => await AttestStagingDirectoryAsync(final, _verifiedRoot, input, packageStream, token, null, reused: true);

    private async Task<QmodStagingResult> AttestStagingDirectoryAsync(
        string root, string boundaryRoot, QmodStagingInput input, Stream packageStream, CancellationToken token,
        Guid? expectedTransactionId, bool reused)
    {
        var metadata = Path.Combine(root, StagingMetadataName);
        try { EnsureStagingPathChain(root, boundaryRoot); }
        catch (StagingException) { return QmodStagingResult.Failure(QmodStagingFailureCode.VerifiedStagingInvalid); }
        if (!File.Exists(metadata)) return QmodStagingResult.Failure(QmodStagingFailureCode.StagingMetadataInvalid);
        if (IsReparse(metadata)) return QmodStagingResult.Failure(QmodStagingFailureCode.StagingMetadataInvalid);
        try
        {
            packageStream.Position = 0;
            using var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = InspectArchive(archive);
            var packageManifest = await ReadPackageManifestAsync(entries.PackageManifest, token);
            var moduleManifest = await ReadModuleManifestAsync(entries.ModuleManifest, token);
            ValidateManifests(packageManifest, moduleManifest, input, entries);
            var stagingMetadata = await ReadAndValidateStagingMetadataAsync(metadata, input, token, expectedTransactionId);
            if (stagingMetadata.FileCount != entries.Files.Count ||
                stagingMetadata.TotalUncompressedBytes != entries.TotalLength)
                return QmodStagingResult.Failure(QmodStagingFailureCode.StagingMetadataInvalid);
            var metadataFiles = stagingMetadata.Files.ToDictionary(file => CanonicalRelativePath(file.RelativePath),
                file => file, StringComparer.Ordinal);
            var verifiedFiles = new List<QmodStagedFile>(entries.Files.Count);
            foreach (var entry in entries.Files)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var source = entry.Entry.Open();
                var size = await CopyBoundedAsync(source, Stream.Null, hash, entry.Entry.Length,
                    _limits.MaximumSingleFileBytes, _limits.MaximumTotalUncompressedBytes, token);
                var sha = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!metadataFiles.TryGetValue(CanonicalRelativePath(entry.RelativePath), out var recorded) ||
                    recorded.Size != size || !recorded.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase))
                    return QmodStagingResult.Failure(QmodStagingFailureCode.StagingMetadataInvalid);
                verifiedFiles.Add(new(entry.RelativePath, size, sha));
            }
            await VerifyExistingTreeAsync(root, boundaryRoot, verifiedFiles, token);
            return new(true, reused, QmodStagingFailureCode.None, root, verifiedFiles.Count, entries.TotalLength);
        }
        catch (MetadataException) { return QmodStagingResult.Failure(QmodStagingFailureCode.StagingMetadataInvalid); }
        catch (TreeException) { return QmodStagingResult.Failure(QmodStagingFailureCode.VerifiedStagingInvalid); }
        catch (Exception exception) when (exception is IOException or JsonException or StagingException or
            FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return QmodStagingResult.Failure(QmodStagingFailureCode.VerifiedStagingInvalid); }
    }

    private async Task WriteMetadataAsync(string root, QmodStagingInput input, IReadOnlyList<QmodStagedFile> files,
        long total, Guid transactionId, CancellationToken token)
    {
        var path = Path.Combine(root, StagingMetadataName);
        var metadata = new
        {
            schemaVersion = 2, moduleId = input.ReleaseIdentity.ModuleId, version = input.ReleaseIdentity.TargetVersion,
            moduleApiVersion = input.ModuleApiVersion, packageSha256 = input.ReleaseIdentity.Sha256.ToLowerInvariant(),
            packageSize = input.ReleaseIdentity.ExpectedSize, stagedAtUtc = _timeProvider.GetUtcNow(),
            sourceIdentity = input.SourceIdentity, environmentIdentity = _environmentIdentity,
            officialReleaseIdentityHash = HashOfficialReleaseIdentity(input.ReleaseIdentity.OfficialUrl),
            transactionId,
            fileCount = files.Count, totalUncompressedBytes = total,
            files = files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).Select(file => new
            { relativePath = file.RelativePath, size = file.Size, sha256 = file.Sha256 })
        };
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, metadata, cancellationToken: token);
        await stream.FlushAsync(token);
        stream.Flush(true);
    }

    private async Task<StagingMetadata> ReadAndValidateStagingMetadataAsync(
        string path, QmodStagingInput input, CancellationToken token, Guid? expectedTransactionId = null)
    {
        try
        {
            byte[] bytes;
            var physicalRoot = GetPhysicalDirectoryPath(Path.GetDirectoryName(path)!);
            await using (var stream = OpenAttestedTreeFile(path, physicalRoot))
            {
                if (stream.Length is <= 0 or > 1024 * 1024) throw new MetadataException();
                bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, token);
                if (stream.Length != bytes.Length) throw new MetadataException();
            }
            using var document = ParseStrictJson(bytes, new HashSet<string>(StringComparer.Ordinal)
            {
                "schemaVersion", "moduleId", "version", "moduleApiVersion", "packageSha256", "packageSize",
                "stagedAtUtc", "sourceIdentity", "environmentIdentity", "officialReleaseIdentityHash",
                "fileCount", "totalUncompressedBytes", "files", "transactionId"
            });
            var root = document.RootElement;
            if (RequiredInt(root, "schemaVersion") != 2 ||
                RequiredText(root, "moduleId") != input.ReleaseIdentity.ModuleId ||
                RequiredText(root, "version") != input.ReleaseIdentity.TargetVersion ||
                RequiredText(root, "moduleApiVersion") != input.ModuleApiVersion ||
                !RequiredText(root, "packageSha256").Equals(input.ReleaseIdentity.Sha256, StringComparison.OrdinalIgnoreCase) ||
                RequiredLong(root, "packageSize") != input.ReleaseIdentity.ExpectedSize ||
                RequiredText(root, "sourceIdentity") != input.SourceIdentity ||
                RequiredText(root, "environmentIdentity") != _environmentIdentity ||
                RequiredText(root, "officialReleaseIdentityHash") != HashOfficialReleaseIdentity(input.ReleaseIdentity.OfficialUrl) ||
                !Guid.TryParseExact(RequiredText(root, "transactionId"), "D", out var transactionId) ||
                (expectedTransactionId.HasValue && transactionId != expectedTransactionId.Value) ||
                !DateTimeOffset.TryParse(RequiredText(root, "stagedAtUtc"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out _) ||
                !root.TryGetProperty("files", out var fileArray) || fileArray.ValueKind != JsonValueKind.Array)
                throw new MetadataException();
            var fileCount = RequiredInt(root, "fileCount");
            var total = RequiredLong(root, "totalUncompressedBytes");
            if (fileCount < 0 || total < 0 || fileArray.GetArrayLength() != fileCount) throw new MetadataException();
            var files = new List<QmodStagedFile>(fileCount);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in fileArray.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object) throw new MetadataException();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in file.EnumerateObject())
                    if (!names.Add(property.Name) || property.Name is not ("relativePath" or "size" or "sha256"))
                        throw new MetadataException();
                if (names.Count != 3) throw new MetadataException();
                var relative = RequiredText(file, "relativePath");
                ValidateMetadataRelativePath(relative);
                var size = RequiredLong(file, "size");
                var sha = RequiredText(file, "sha256");
                if (size < 0 || sha.Length != 64 || !sha.All(Uri.IsHexDigit) ||
                    !paths.Add(CanonicalRelativePath(relative))) throw new MetadataException();
                files.Add(new(relative, size, sha.ToLowerInvariant()));
            }
            if (files.Sum(file => file.Size) != total) throw new MetadataException();
            return new(transactionId, fileCount, total, files);
        }
        catch (OperationCanceledException) { throw; }
        catch (MetadataException) { throw; }
        catch (TreeException) { throw new MetadataException(); }
        catch (Exception exception) when (exception is IOException or JsonException or StagingException or
            FormatException or InvalidOperationException or OverflowException or ArgumentException)
        { throw new MetadataException(); }
    }

    private async Task VerifyExistingTreeAsync(string root, string boundaryRoot,
        IReadOnlyList<QmodStagedFile> files, CancellationToken token)
    {
        EnsureStagingPathChain(root, boundaryRoot);
        var physicalRoot = GetPhysicalDirectoryPath(root);
        var expectedFiles = files.ToDictionary(file => CanonicalRelativePath(file.RelativePath), file => file, StringComparer.Ordinal);
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var segments = file.RelativePath.Split('/');
            for (var i = 1; i < segments.Length; i++)
                expectedDirectories.Add(CanonicalRelativePath(string.Join('/', segments[..i])));
        }
        var observedFiles = new HashSet<string>(StringComparer.Ordinal);
        var observedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var handles = new List<SafeFileHandle>();
        try
        {
            var rootHandle = OpenAttestedDirectory(root, physicalRoot);
            handles.Add(rootHandle);
            var pending = new Queue<string>();
            pending.Enqueue(root);
            while (pending.TryDequeue(out var directory))
            {
                token.ThrowIfCancellationRequested();
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    EnsureWithin(root, entry);
                    if (IsReparse(entry)) throw new TreeException();
                    var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                    var canonical = CanonicalRelativePath(relative);
                    if (Directory.Exists(entry))
                    {
                        if (!expectedDirectories.Contains(canonical) || !observedDirectories.Add(canonical)) throw new TreeException();
                        handles.Add(OpenAttestedDirectory(entry, physicalRoot));
                        pending.Enqueue(entry);
                    }
                    else if (File.Exists(entry))
                    {
                        if (relative.Equals(StagingMetadataName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!expectedFiles.TryGetValue(canonical, out var expected) || !observedFiles.Add(canonical)) throw new TreeException();
                        await using var stream = OpenAttestedTreeFile(entry, physicalRoot);
                        var lengthBefore = stream.Length;
                        if (lengthBefore != expected.Size) throw new TreeException();
                        var hash = await SHA256.HashDataAsync(stream, token);
                        if (stream.Length != lengthBefore ||
                            !CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected.Sha256))) throw new TreeException();
                    }
                    else throw new TreeException();
                }
            }
        }
        finally { foreach (var handle in handles) handle.Dispose(); }
        if (observedFiles.Count != expectedFiles.Count || observedDirectories.Count != expectedDirectories.Count)
            throw new TreeException();
    }

    private static SafeFileHandle OpenAttestedDirectory(string path, string physicalRoot)
    {
        var handle = NativeMethods.OpenDirectory(path);
        if (handle.IsInvalid || NativeMethods.IsReparseHandle(handle)) { handle.Dispose(); throw new TreeException(); }
        var final = NativeMethods.GetFinalPath(handle);
        if (!final.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) || !IsWithin(physicalRoot, final))
        { handle.Dispose(); throw new TreeException(); }
        return handle;
    }

    private static FileStream OpenAttestedTreeFile(string path, string physicalRoot)
    {
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (NativeMethods.IsReparseHandle(stream.SafeFileHandle)) throw new TreeException();
            var final = NativeMethods.GetFinalPath(stream.SafeFileHandle);
            if (!final.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) || !IsWithin(physicalRoot, final))
                throw new TreeException();
            return stream;
        }
        catch { stream?.Dispose(); throw; }
    }

    private void EnsureStagingPathChain(string root, string boundaryRoot)
    {
        EnsureWithin(boundaryRoot, root);
        EnsureNoReparseAncestors(_stagingRoot);
        var current = boundaryRoot;
        EnsureOrdinaryDirectory(current);
        foreach (var segment in Path.GetRelativePath(boundaryRoot, root).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            EnsureOrdinaryDirectory(current);
        }
    }

    private static string CanonicalRelativePath(string value) =>
        value.Replace('\\', '/').Normalize(NormalizationForm.FormC).ToUpperInvariant();

    private static void ValidateMetadataRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\') || value.StartsWith('/') || value.EndsWith('/'))
            throw new MetadataException();
        var segments = value.Split('/', StringSplitOptions.None);
        if (segments.Length == 0 || segments.Any(segment => !IsSafeSegment(segment)) ||
            value != value.Normalize(NormalizationForm.FormC)) throw new MetadataException();
    }

    private void EnsureRootChain()
    {
        foreach (var path in new[] { _stagingRoot, _incomingRoot, _verifiedRoot, _locksRoot })
            CreateOrdinaryDirectoryTree(path);
        EnsureNoReparseAncestors(_stagingRoot);
        if (_userModulesRoot is not null && Directory.Exists(_userModulesRoot))
        {
            EnsureNoReparseAncestors(_userModulesRoot);
            var staging = GetPhysicalDirectoryPath(_stagingRoot);
            var userModules = GetPhysicalDirectoryPath(_userModulesRoot);
            if (IsWithin(staging, userModules) || IsWithin(userModules, staging))
                throw new StagingException(QmodStagingFailureCode.PackageChanged);
        }
    }

    private async Task<PublicationLease> AcquirePublicationLockAsync(QmodStagingInput input, CancellationToken token)
    {
        var key = BuildModuleVersionKey(input.ReleaseIdentity.ModuleId, input.ReleaseIdentity.TargetVersion);
        var local = await PublicationGate.AcquireAsync(key, token);
        try
        {
            var lockPath = BuildPublicationLockPath(key);
            EnsureSafeParents(_stagingRoot, _locksRoot);
            var crossProcess = await SecureWindowsFileSystem.AcquireLockAsync(
                lockPath, _physicalLocksRoot, _locksRootIdentity, token);
            if (crossProcess.Recovered) Log("Publication lock recovered after process exit", input);
            return new(local, crossProcess, _testHooks?.PublicationLockMarkerCleanup,
                () => Log("Publication lock marker cleanup failed", input, failure: QmodStagingFailureCode.IoFailure));
        }
        catch
        {
            await local.DisposeAsync();
            throw;
        }
    }

    private QmodModuleVersionKey BuildModuleVersionKey(string moduleId, string targetVersion) =>
        new(GetPhysicalDirectoryPath(_stagingRoot).ToUpperInvariant(),
            _environmentIdentity, moduleId, targetVersion);

    private string BuildPublicationLockPath(QmodModuleVersionKey key)
    {
        var identity = Encoding.UTF8.GetBytes($"{key.StagingRootIdentity}\0{key.EnvironmentIdentity}\0{key.ModuleId}\0{key.TargetVersion}");
        var path = Path.Combine(_locksRoot, Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant() + ".lock");
        EnsureWithin(_locksRoot, path);
        return path;
    }

    internal string GetPublicationLockPathForTest(string moduleId, string targetVersion) =>
        GetPublicationLockPathForTestCore(moduleId, targetVersion);

    private string GetPublicationLockPathForTestCore(string moduleId, string targetVersion)
    {
        CreateOrdinaryDirectoryTree(_stagingRoot);
        CreateOrdinaryDirectoryTree(_locksRoot);
        return BuildPublicationLockPath(BuildModuleVersionKey(moduleId, targetVersion));
    }

    private static bool HasConflictingVersionContent(string versionRoot, string final)
    {
        if (!Directory.Exists(versionRoot)) return File.Exists(versionRoot);
        if (IsReparse(versionRoot)) throw new StagingException(QmodStagingFailureCode.UnsupportedEntryType);
        foreach (var entry in Directory.EnumerateFileSystemEntries(versionRoot))
            if (!Path.GetFullPath(entry).Equals(Path.GetFullPath(final), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void CreateOrdinaryDirectoryTree(string path)
    {
        var missing = new Stack<string>();
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            if (File.Exists(current)) throw new StagingException(QmodStagingFailureCode.PathCollision);
            missing.Push(current);
            current = Path.GetDirectoryName(current) ?? throw new StagingException(QmodStagingFailureCode.UnsafeEntryPath);
        }
        EnsureOrdinaryDirectory(current);
        while (missing.TryPop(out var directory))
        {
            Directory.CreateDirectory(directory);
            EnsureOrdinaryDirectory(directory);
        }
    }

    private static void CreateSafeDirectoryChain(string root, string targetDirectory)
    {
        EnsureWithin(root, targetDirectory);
        var current = Path.GetFullPath(root);
        EnsureOrdinaryDirectory(current);
        var relative = Path.GetRelativePath(current, Path.GetFullPath(targetDirectory));
        if (relative == ".") return;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current)) throw new StagingException(QmodStagingFailureCode.PathCollision);
            if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            EnsureOrdinaryDirectory(current);
        }
    }

    private static void EnsureSafeParents(string root, string targetDirectory)
    {
        EnsureWithin(root, targetDirectory);
        var current = Path.GetFullPath(root);
        EnsureOrdinaryDirectory(current);
        var relative = Path.GetRelativePath(current, Path.GetFullPath(targetDirectory));
        if (relative == ".") return;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current)) throw new StagingException(QmodStagingFailureCode.PathCollision);
            if (Directory.Exists(current)) EnsureOrdinaryDirectory(current);
        }
    }

    private static string ResolveTarget(string root, string relative)
    {
        var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureWithin(root, target);
        return target;
    }

    private static void EnsureWithin(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalized = Path.GetFullPath(path);
        if (!normalized.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new StagingException(QmodStagingFailureCode.UnsafeEntryPath);
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalized = Path.GetFullPath(path);
        return normalized.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void ValidateRootIsolation()
    {
        if (_userModulesRoot is not null &&
            (IsWithin(_stagingRoot, _userModulesRoot) || IsWithin(_userModulesRoot, _stagingRoot)))
            throw new QmodStagingConfigurationException(QmodStagingConfigurationFailureCode.OverlappingRoots,
                "Staging and user module roots must be disjoint.");
        try
        {
            EnsureNoReparseAncestorsIfPresent(_stagingRoot);
        }
        catch (StagingException exception) when (exception.Code == QmodStagingFailureCode.UnsupportedEntryType)
        {
            throw new QmodStagingConfigurationException(QmodStagingConfigurationFailureCode.UnsafeStagingRoot,
                "Staging root must not contain reparse points.");
        }
        if (_userModulesRoot is null) return;
        try { EnsureNoReparseAncestorsIfPresent(_userModulesRoot); }
        catch (StagingException exception) when (exception.Code == QmodStagingFailureCode.UnsupportedEntryType)
        {
            throw new QmodStagingConfigurationException(QmodStagingConfigurationFailureCode.UnsafeUserModulesRoot,
                "User modules root must not contain reparse points.");
        }
        if (Directory.Exists(_stagingRoot) && Directory.Exists(_userModulesRoot))
        {
            var physicalStaging = GetPhysicalDirectoryPath(_stagingRoot);
            var physicalUserModules = GetPhysicalDirectoryPath(_userModulesRoot);
            if (IsWithin(physicalStaging, physicalUserModules) || IsWithin(physicalUserModules, physicalStaging))
                throw new QmodStagingConfigurationException(QmodStagingConfigurationFailureCode.OverlappingRoots,
                    "Staging and user module roots must be physically disjoint.");
        }
    }

    private FileStream OpenAndAttestPackage(string packagePath)
    {
        if (!File.Exists(packagePath)) throw new StagingException(QmodStagingFailureCode.PackageMissing);
        try
        {
            EnsureNoReparseAncestors(Path.GetDirectoryName(packagePath)!);
        }
        catch (StagingException exception) when (exception.Code == QmodStagingFailureCode.UnsupportedEntryType)
        {
            throw new StagingException(QmodStagingFailureCode.PackageChanged);
        }
        if (IsReparse(packagePath)) throw new StagingException(QmodStagingFailureCode.PackageChanged);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var physicalPackage = NativeMethods.GetFinalPath(stream.SafeFileHandle);
            if (!physicalPackage.Equals(Path.GetFullPath(packagePath), StringComparison.OrdinalIgnoreCase))
                throw new StagingException(QmodStagingFailureCode.PackageChanged);
            var physicalStaging = GetPhysicalDirectoryPath(_stagingRoot);
            if (IsWithin(physicalStaging, physicalPackage)) throw new StagingException(QmodStagingFailureCode.PackageChanged);
            if (_userModulesRoot is not null && Directory.Exists(_userModulesRoot) &&
                IsWithin(GetPhysicalDirectoryPath(_userModulesRoot), physicalPackage))
                throw new StagingException(QmodStagingFailureCode.PackageChanged);
            return stream;
        }
        catch { stream?.Dispose(); throw; }
    }

    private static string GetPhysicalDirectoryPath(string path)
    {
        using var handle = NativeMethods.OpenDirectory(path);
        if (handle.IsInvalid) throw new IOException($"Directory attestation failed with Win32 error {Marshal.GetLastPInvokeError()}.");
        if (NativeMethods.IsReparseHandle(handle)) throw new StagingException(QmodStagingFailureCode.UnsupportedEntryType);
        return NativeMethods.GetFinalPath(handle);
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        if (!Directory.Exists(path) || IsReparse(path)) throw new StagingException(QmodStagingFailureCode.UnsupportedEntryType);
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (Directory.Exists(current) && IsReparse(current))
                throw new StagingException(QmodStagingFailureCode.UnsupportedEntryType);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
    }

    private static void EnsureNoReparseAncestorsIfPresent(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
            current = Path.GetDirectoryName(current) ?? throw new StagingException(QmodStagingFailureCode.UnsafeEntryPath);
        EnsureNoReparseAncestors(current);
    }

    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private void DeletePartialTree(string partial)
    {
        EnsureWithin(_incomingRoot, partial);
        if (IsReparse(partial)) { Directory.Delete(partial, false); return; }
        foreach (var entry in Directory.EnumerateFileSystemEntries(partial))
        {
            if (Directory.Exists(entry))
            {
                if (IsReparse(entry)) Directory.Delete(entry, false);
                else DeletePartialTree(entry);
            }
            else File.Delete(entry);
        }
        Directory.Delete(partial, false);
    }

    private static async Task<byte[]> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return await SHA256.HashDataAsync(stream, token);
    }

    private static async Task<byte[]> ReadBoundedAsync(ZipArchiveEntry entry, int maximum, CancellationToken token)
    {
        if (entry.Length > maximum) throw new StagingException(QmodStagingFailureCode.ManifestInvalid);
        await using var stream = entry.Open();
        using var memory = new MemoryStream();
        await CopyBoundedAsync(stream, memory, null, entry.Length, maximum, maximum, token);
        return memory.ToArray();
    }

    private static async Task<long> CopyBoundedAsync(Stream source, Stream destination, IncrementalHash? hash,
        long declaredLength, long singleLimit, long remainingTotal, CancellationToken token)
    {
        var buffer = new byte[65536];
        long written = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read == 0) break;
            written = checked(written + read);
            if (written > declaredLength) throw new StagingException(QmodStagingFailureCode.InvalidArchive);
            if (written > singleLimit) throw new StagingException(QmodStagingFailureCode.SingleFileLimitExceeded);
            if (written > remainingTotal) throw new StagingException(QmodStagingFailureCode.TotalSizeLimitExceeded);
            hash?.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
        if (written != declaredLength) throw new StagingException(QmodStagingFailureCode.InvalidArchive);
        return written;
    }

    private QmodStagingResult Reject(QmodStagingFailureCode code, QmodStagingInput input)
    {
        Log("Staging rejected", input, failure: code);
        return QmodStagingResult.Failure(code);
    }

    private void Log(string name, QmodStagingInput input, int entries = 0, long total = 0,
        QmodStagingFailureCode failure = QmodStagingFailureCode.None)
    {
        var moduleId = IsSafeModuleId(input.ReleaseIdentity.ModuleId) ? input.ReleaseIdentity.ModuleId : "invalid";
        var version = input.ReleaseIdentity.TargetVersion.Length <= 128 &&
                      SemanticVersion.TryParse(input.ReleaseIdentity.TargetVersion, out _)
            ? input.ReleaseIdentity.TargetVersion : "invalid";
        try { _log?.Invoke(new(name, moduleId, version, SafeHashPrefix(input.ReleaseIdentity.Sha256), failure, entries, total)); }
        catch { }
    }

    private static string SafeHashPrefix(string hash) => hash.Length >= 12 && hash.All(Uri.IsHexDigit) ? hash[..12].ToLowerInvariant() : "invalid";
    private static bool IsSafeFileName(string value) => !string.IsNullOrWhiteSpace(value) &&
        value == Path.GetFileName(value) && IsSafeSegment(value);
    private static bool IsSafeModuleId(string value) => SecureModuleIdentity.Validate(value) == ModuleIdentityFailure.None;
    private static string RequiredText(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new StagingException(QmodStagingFailureCode.ManifestInvalid);
    private static int RequiredInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
            ? number : throw new StagingException(QmodStagingFailureCode.ManifestInvalid);
    private static long RequiredLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : throw new StagingException(QmodStagingFailureCode.ManifestInvalid);

    public async ValueTask DisposeAsync()
    {
        var ownsDisposal = false;
        lock (_lifecycleSync)
        {
            if (_disposeState == 0) { _disposeState = 1; ownsDisposal = true; LogService("Service disposal started"); }
        }
        if (!ownsDisposal) { await _disposeCompletion.Task; return; }
        try
        {
            _lifetime.Cancel();
            try { await Task.WhenAll(_inflight.Values.Select(operation => operation.Task)); } catch { }
            _parallelism.Dispose();
            _lifetime.Dispose();
            lock (_lifecycleSync) _disposeState = 2;
            LogService("Service disposal completed");
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
            throw;
        }
    }

    private void LogService(string name)
    {
        try { _log?.Invoke(new(name, "service", "0.0.0", "000000000000")); }
        catch { }
    }

    private enum EntryKind { File, Directory }
    private sealed record InspectedEntry(ZipArchiveEntry Entry, string RelativePath, bool IsDirectory);
    private sealed record ArchiveInspection(IReadOnlyList<InspectedEntry> Files, InspectedEntry PackageManifest,
        InspectedEntry ModuleManifest, long TotalLength);
    private sealed record PackageManifest(int SchemaVersion, string ModuleId, string Version, string ModuleApiVersion, string EntryManifest);
    private sealed record ModuleIdentityManifest(string Id, string Version, string Entry);
    private sealed class StagingException(QmodStagingFailureCode code) : Exception { public QmodStagingFailureCode Code { get; } = code; }
    private sealed class MetadataException : Exception;
    private sealed class TreeException : Exception;
    private sealed record StagingMetadata(Guid TransactionId, int FileCount, long TotalUncompressedBytes,
        IReadOnlyList<QmodStagedFile> Files);
    private static class NativeMethods
    {
        private const uint FileReadAttributes = 0x80;
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const int FileAttributeTagInfo = 9;

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass,
            out FileAttributeTagInformation information, uint bufferSize);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path,
            uint pathLength, uint flags);

        public static SafeFileHandle OpenDirectory(string path) => CreateFile(path, FileReadAttributes,
            FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);

        public static bool IsReparseHandle(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo, out var information,
                (uint)Marshal.SizeOf<FileAttributeTagInformation>()))
                throw new IOException($"Handle attribute query failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            return (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0;
        }

        public static string GetFinalPath(SafeFileHandle handle)
        {
            var buffer = new StringBuilder(512);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0) throw new IOException($"Final path query failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            if (length >= buffer.Capacity)
            {
                buffer.EnsureCapacity(checked((int)length + 1));
                length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity)
                    throw new IOException($"Final path query failed with Win32 error {Marshal.GetLastPInvokeError()}.");
            }
            var value = buffer.ToString();
            if (value.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) value = "\\\\" + value[8..];
            else if (value.StartsWith("\\\\?\\", StringComparison.Ordinal)) value = value[4..];
            return Path.GetFullPath(value);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInformation { public uint FileAttributes; public uint ReparseTag; }
    }
    private sealed class QmodStagingOperationKeyComparer : IEqualityComparer<QmodStagingOperationKey>
    {
        public static QmodStagingOperationKeyComparer Instance { get; } = new();
        public bool Equals(QmodStagingOperationKey x, QmodStagingOperationKey y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.PackagePath, y.PackagePath) &&
            StringComparer.Ordinal.Equals(x.ModuleId, y.ModuleId) &&
            StringComparer.Ordinal.Equals(x.TargetVersion, y.TargetVersion) &&
            StringComparer.Ordinal.Equals(x.FileName, y.FileName) && x.ExpectedSize == y.ExpectedSize &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Sha256, y.Sha256) &&
            StringComparer.Ordinal.Equals(x.ModuleApiVersion, y.ModuleApiVersion) &&
            StringComparer.Ordinal.Equals(x.SourceIdentity, y.SourceIdentity) &&
            StringComparer.Ordinal.Equals(x.EnvironmentIdentity, y.EnvironmentIdentity) &&
            StringComparer.Ordinal.Equals(x.OfficialReleaseIdentityHash, y.OfficialReleaseIdentityHash);
        public int GetHashCode(QmodStagingOperationKey value)
        {
            var hash = new HashCode();
            hash.Add(value.PackagePath, StringComparer.OrdinalIgnoreCase);
            hash.Add(value.ModuleId, StringComparer.Ordinal);
            hash.Add(value.TargetVersion, StringComparer.Ordinal);
            hash.Add(value.FileName, StringComparer.Ordinal);
            hash.Add(value.ExpectedSize);
            hash.Add(value.Sha256, StringComparer.OrdinalIgnoreCase);
            hash.Add(value.ModuleApiVersion, StringComparer.Ordinal);
            hash.Add(value.SourceIdentity, StringComparer.Ordinal);
            hash.Add(value.EnvironmentIdentity, StringComparer.Ordinal);
            hash.Add(value.OfficialReleaseIdentityHash, StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }
    private static class PublicationGate
    {
        private static readonly object Sync = new();
        private static readonly Dictionary<QmodModuleVersionKey, GateEntry> Entries = [];
        public static async Task<GateLease> AcquireAsync(QmodModuleVersionKey key, CancellationToken token)
        {
            GateEntry entry;
            lock (Sync)
            {
                if (!Entries.TryGetValue(key, out entry!)) Entries.Add(key, entry = new());
                entry.References++;
            }
            try { await entry.Semaphore.WaitAsync(token); return new(key, entry); }
            catch { ReleaseReference(key, entry, acquired: false); throw; }
        }
        private static void ReleaseReference(QmodModuleVersionKey key, GateEntry entry, bool acquired)
        {
            if (acquired) entry.Semaphore.Release();
            lock (Sync)
            {
                entry.References--;
                if (entry.References == 0 && Entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                { Entries.Remove(key); entry.Semaphore.Dispose(); }
            }
        }
        public sealed class GateEntry { public SemaphoreSlim Semaphore { get; } = new(1, 1); public int References; }
        public sealed class GateLease(QmodModuleVersionKey key, GateEntry entry) : IAsyncDisposable
        {
            private int _disposed;
            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) ReleaseReference(key, entry, acquired: true);
                return ValueTask.CompletedTask;
            }
        }
    }
    private sealed class PublicationLease(PublicationGate.GateLease local, CrashRecoverableFileLock crossProcess,
        Action? markerCleanupHook, Action markerCleanupWarning) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                try
                {
                    markerCleanupHook?.Invoke();
                    RandomAccess.SetLength(crossProcess.Handle, 0);
                    RandomAccess.FlushToDisk(crossProcess.Handle);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    markerCleanupWarning();
                }
                finally { await crossProcess.DisposeAsync(); }
            }
            finally { await local.DisposeAsync(); }
        }
    }
    private sealed class StagingOperation(CancellationTokenSource cancellation, Func<Task<QmodStagingResult>> factory) : IDisposable
    {
        private readonly object _commitSync = new();
        private readonly TaskCompletionSource<bool> _commitDecision =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CommitState _commitState;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        private readonly Lazy<Task<QmodStagingResult>> _task = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        public Task<QmodStagingResult> Task => _task.Value;
        public void CommitPublication(Action move)
        {
            lock (_commitSync)
            {
                _commitState = CommitState.Committing;
                try
                {
                    move();
                    _commitState = CommitState.Committed;
                    _commitDecision.TrySetResult(true);
                }
                catch
                {
                    _commitState = CommitState.Failed;
                    _commitDecision.TrySetResult(false);
                    throw;
                }
            }
        }
        public async Task<bool> ObserveCommitAtCancellationBoundaryAsync()
        {
            Task<bool>? pending = null;
            lock (_commitSync)
            {
                if (_commitState == CommitState.Committed) return true;
                if (_commitState == CommitState.Committing) pending = _commitDecision.Task;
            }
            return pending is not null && await pending;
        }
        public void Dispose() => Cancellation.Dispose();
        private enum CommitState { NotStarted, Committing, Committed, Failed }
    }
}
