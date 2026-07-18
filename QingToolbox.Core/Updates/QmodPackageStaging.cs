using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
    string EnvironmentIdentity);

public readonly record struct QmodModuleVersionKey(string ModuleId, string TargetVersion);

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

public sealed class QmodPackageStagingService : IAsyncDisposable
{
    public const string PackageManifestName = "qmod.json";
    public const string StagingMetadataName = "qmod-staging.json";
    private readonly string _stagingRoot;
    private readonly string _incomingRoot;
    private readonly string _verifiedRoot;
    private readonly string? _userModulesRoot;
    private readonly string _environmentIdentity;
    private readonly QmodStagingLimits _limits;
    private readonly TimeProvider _timeProvider;
    private readonly Action<QmodStagingLogEvent>? _log;
    private readonly SemaphoreSlim _parallelism;
    private readonly ConcurrentDictionary<QmodStagingOperationKey, StagingOperation> _inflight =
        new(QmodStagingOperationKeyComparer.Instance);
    private readonly CancellationTokenSource _lifetime = new();

    public QmodPackageStagingService(string stagingRoot, TimeProvider timeProvider, string environmentIdentity,
        string? userModulesRoot = null, QmodStagingLimits? limits = null,
        Action<QmodStagingLogEvent>? log = null, int maximumParallelism = 2)
    {
        if (!Path.IsPathFullyQualified(stagingRoot)) throw new ArgumentException("Staging root must be absolute.", nameof(stagingRoot));
        if (maximumParallelism <= 0) throw new ArgumentOutOfRangeException(nameof(maximumParallelism));
        _stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        if (Path.GetPathRoot(_stagingRoot) == _stagingRoot) throw new ArgumentException("Staging root cannot be a volume root.", nameof(stagingRoot));
        _incomingRoot = Path.Combine(_stagingRoot, "Incoming");
        _verifiedRoot = Path.Combine(_stagingRoot, "Verified");
        _environmentIdentity = environmentIdentity is "Production" or "Development" or "ModuleTest"
            ? environmentIdentity : throw new ArgumentException("Unsupported execution environment.", nameof(environmentIdentity));
        _userModulesRoot = string.IsNullOrWhiteSpace(userModulesRoot)
            ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(userModulesRoot));
        _timeProvider = timeProvider;
        _limits = limits ?? new();
        _limits.Validate();
        _log = log;
        _parallelism = new(maximumParallelism, maximumParallelism);
    }

    public Task<QmodStagingResult> StageAsync(QmodStagingInput input, CancellationToken callerToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        QmodStagingOperationKey key;
        try { key = ValidateStaticInputAndCreateKey(input); }
        catch (StagingException exception) { return Task.FromResult(Reject(exception.Code, input)); }
        StagingOperation? candidate = null;
        candidate = new(CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token),
            () => RunAndRemoveAsync(key, input, candidate!));
        var operation = _inflight.GetOrAdd(key, candidate);
        if (!ReferenceEquals(operation, candidate))
        {
            candidate.Dispose();
            Log("Shared operation joined", input);
        }
        return callerToken.CanBeCanceled ? operation.Task.WaitAsync(callerToken) : operation.Task;
    }

    public void Cancel(QmodStagingInput input)
    {
        try
        {
            var key = ValidateStaticInputAndCreateKey(input);
            if (_inflight.TryGetValue(key, out var operation)) operation.Cancellation.Cancel();
        }
        catch (StagingException) { }
    }

    private async Task<QmodStagingResult> RunAndRemoveAsync(QmodStagingOperationKey key, QmodStagingInput input, StagingOperation operation)
    {
        try { return await StageCoreAsync(input, operation.Cancellation.Token); }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<QmodStagingOperationKey, StagingOperation>(key, operation));
            operation.Dispose();
        }
    }

    private async Task<QmodStagingResult> StageCoreAsync(QmodStagingInput input, CancellationToken token)
    {
        string? partial = null;
        Log("Staging started", input);
        try
        {
            var packagePath = Path.GetFullPath(input.VerifiedPackage.FilePath);
            if (!File.Exists(packagePath)) return Reject(QmodStagingFailureCode.PackageMissing, input);
            if (IsReparse(packagePath)) return Reject(QmodStagingFailureCode.PackageChanged, input);
            await using var packageStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (packageStream.Length != input.ReleaseIdentity.ExpectedSize)
                return Reject(QmodStagingFailureCode.PackageSizeMismatch, input);
            var packageHash = await SHA256.HashDataAsync(packageStream, token);
            if (!CryptographicOperations.FixedTimeEquals(packageHash, Convert.FromHexString(input.ReleaseIdentity.Sha256)))
                return Reject(QmodStagingFailureCode.PackageHashMismatch, input);
            packageStream.Position = 0;

            await _parallelism.WaitAsync(token);
            try
            {
                EnsureRootChain();
                Log("Module/version publication lock waiting", input);
                await using var publicationLock = await AcquirePublicationLockAsync(input, token);
                Log("Publication lock acquired", input);
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
                await WriteMetadataAsync(partial, input, files, actualTotal, token);
                VerifyExtractedTree(partial, files);
                CreateSafeDirectoryChain(_verifiedRoot, versionRoot);
                EnsureSafeParents(_verifiedRoot, versionRoot);
                token.ThrowIfCancellationRequested();
                if (HasConflictingVersionContent(versionRoot, final))
                    return Reject(QmodStagingFailureCode.StagingConflict, input);
                try { Directory.Move(partial, final); }
                catch (IOException) when (Directory.Exists(final) || HasConflictingVersionContent(versionRoot, final))
                {
                    return Reject(QmodStagingFailureCode.StagingConflict, input);
                }
                partial = null;
                Log("Verified directory published", input, files.Count, actualTotal);
                packageStream.Position = 0;
                var published = await ValidateExistingStagingAsync(final, input, packageStream, token);
                if (!published.Succeeded) return Reject(published.FailureCode, input);
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
        return new(packagePath, identity.ModuleId, identity.TargetVersion, identity.FileName,
            identity.ExpectedSize, identity.Sha256.ToLowerInvariant(), input.ModuleApiVersion,
            input.SourceIdentity, _environmentIdentity);
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
    {
        var metadata = Path.Combine(final, StagingMetadataName);
        try { EnsureVerifiedPathChain(final); }
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
            var stagingMetadata = await ReadAndValidateStagingMetadataAsync(metadata, input, token);
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
            await VerifyExistingTreeAsync(final, verifiedFiles, token);
            return new(true, true, QmodStagingFailureCode.None, final, verifiedFiles.Count, entries.TotalLength);
        }
        catch (MetadataException) { return QmodStagingResult.Failure(QmodStagingFailureCode.StagingMetadataInvalid); }
        catch (TreeException) { return QmodStagingResult.Failure(QmodStagingFailureCode.VerifiedStagingInvalid); }
        catch (Exception exception) when (exception is IOException or JsonException or StagingException or
            FormatException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        { return QmodStagingResult.Failure(QmodStagingFailureCode.VerifiedStagingInvalid); }
    }

    private async Task WriteMetadataAsync(string root, QmodStagingInput input, IReadOnlyList<QmodStagedFile> files, long total, CancellationToken token)
    {
        var path = Path.Combine(root, StagingMetadataName);
        var metadata = new
        {
            schemaVersion = 1, moduleId = input.ReleaseIdentity.ModuleId, version = input.ReleaseIdentity.TargetVersion,
            moduleApiVersion = input.ModuleApiVersion, packageSha256 = input.ReleaseIdentity.Sha256.ToLowerInvariant(),
            packageSize = input.ReleaseIdentity.ExpectedSize, stagedAtUtc = _timeProvider.GetUtcNow(),
            sourceIdentity = input.SourceIdentity, environmentIdentity = _environmentIdentity,
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
        string path, QmodStagingInput input, CancellationToken token)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > 1024 * 1024 || IsReparse(path)) throw new MetadataException();
            byte[] bytes;
            await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                bytes = new byte[checked((int)stream.Length)];
                await stream.ReadExactlyAsync(bytes, token);
            }
            using var document = ParseStrictJson(bytes, new HashSet<string>(StringComparer.Ordinal)
            {
                "schemaVersion", "moduleId", "version", "moduleApiVersion", "packageSha256", "packageSize",
                "stagedAtUtc", "sourceIdentity", "environmentIdentity", "fileCount", "totalUncompressedBytes", "files"
            });
            var root = document.RootElement;
            if (RequiredInt(root, "schemaVersion") != 1 ||
                RequiredText(root, "moduleId") != input.ReleaseIdentity.ModuleId ||
                RequiredText(root, "version") != input.ReleaseIdentity.TargetVersion ||
                RequiredText(root, "moduleApiVersion") != input.ModuleApiVersion ||
                !RequiredText(root, "packageSha256").Equals(input.ReleaseIdentity.Sha256, StringComparison.OrdinalIgnoreCase) ||
                RequiredLong(root, "packageSize") != input.ReleaseIdentity.ExpectedSize ||
                RequiredText(root, "sourceIdentity") != input.SourceIdentity ||
                RequiredText(root, "environmentIdentity") != _environmentIdentity ||
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
            return new(fileCount, total, files);
        }
        catch (OperationCanceledException) { throw; }
        catch (MetadataException) { throw; }
        catch (Exception exception) when (exception is IOException or JsonException or StagingException or
            FormatException or InvalidOperationException or OverflowException or ArgumentException)
        { throw new MetadataException(); }
    }

    private async Task VerifyExistingTreeAsync(string root, IReadOnlyList<QmodStagedFile> files, CancellationToken token)
    {
        EnsureVerifiedPathChain(root);
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
        var pending = new Queue<string>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var directory))
        {
            token.ThrowIfCancellationRequested();
            EnsureWithin(root, directory);
            if (IsReparse(directory)) throw new TreeException();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureWithin(root, entry);
                if (IsReparse(entry)) throw new TreeException();
                var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                var canonical = CanonicalRelativePath(relative);
                if (Directory.Exists(entry))
                {
                    if (!expectedDirectories.Contains(canonical) || !observedDirectories.Add(canonical)) throw new TreeException();
                    pending.Enqueue(entry);
                }
                else if (File.Exists(entry))
                {
                    if (relative.Equals(StagingMetadataName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!expectedFiles.TryGetValue(canonical, out var expected) || !observedFiles.Add(canonical)) throw new TreeException();
                    var info = new FileInfo(entry);
                    if (info.Length != expected.Size) throw new TreeException();
                    var hash = await HashFileAsync(entry, token);
                    if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(expected.Sha256))) throw new TreeException();
                }
                else throw new TreeException();
            }
        }
        if (observedFiles.Count != expectedFiles.Count || observedDirectories.Count != expectedDirectories.Count)
            throw new TreeException();
    }

    private void EnsureVerifiedPathChain(string final)
    {
        EnsureWithin(_verifiedRoot, final);
        EnsureNoReparseAncestors(_stagingRoot);
        var current = _verifiedRoot;
        EnsureOrdinaryDirectory(current);
        foreach (var segment in Path.GetRelativePath(_verifiedRoot, final).Split(Path.DirectorySeparatorChar))
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

    private static void VerifyExtractedTree(string root, IReadOnlyList<QmodStagedFile> files)
    {
        var expected = files.Select(file => file.RelativePath.Replace('/', Path.DirectorySeparatorChar))
            .Append(StagingMetadataName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (IsReparse(path)) throw new StagingException(QmodStagingFailureCode.UnsupportedEntryType);
            if (File.Exists(path) && !expected.Remove(Path.GetRelativePath(root, path)))
                throw new StagingException(QmodStagingFailureCode.PathCollision);
        }
        if (expected.Count != 0) throw new StagingException(QmodStagingFailureCode.IoFailure);
    }

    private void EnsureRootChain()
    {
        foreach (var path in new[] { _stagingRoot, _incomingRoot, _verifiedRoot })
            CreateOrdinaryDirectoryTree(path);
    }

    private async Task<PublicationLease> AcquirePublicationLockAsync(QmodStagingInput input, CancellationToken token)
    {
        var key = new QmodModuleVersionKey(input.ReleaseIdentity.ModuleId, input.ReleaseIdentity.TargetVersion);
        var local = await PublicationGate.AcquireAsync(key, token);
        try
        {
            var name = BuildPublicationSemaphoreName(_stagingRoot, _environmentIdentity,
                input.ReleaseIdentity.ModuleId, input.ReleaseIdentity.TargetVersion);
            var semaphore = new Semaphore(1, 1, name, out _);
            try { await WaitForSemaphoreAsync(semaphore, token); }
            catch { semaphore.Dispose(); throw; }
            return new(local, semaphore);
        }
        catch
        {
            await local.DisposeAsync();
            throw;
        }
    }

    public static string BuildPublicationSemaphoreName(string stagingRoot, string environmentIdentity,
        string moduleId, string targetVersion)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot)).ToUpperInvariant();
        var identity = Encoding.UTF8.GetBytes($"{canonicalRoot}\0{environmentIdentity}\0{moduleId}\0{targetVersion}");
        return "Local\\QingToolbox.QmodStaging." + Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
    }

    private static async Task WaitForSemaphoreAsync(Semaphore semaphore, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = token.Register(() => completion.TrySetCanceled(token));
        var registered = ThreadPool.RegisterWaitForSingleObject(semaphore, (_, timedOut) =>
        {
            if (timedOut) completion.TrySetException(new TimeoutException());
            else if (!completion.TrySetResult()) semaphore.Release();
        }, null, Timeout.Infinite, executeOnlyOnce: true);
        try { await completion.Task; }
        finally
        {
            using var unregistered = new ManualResetEvent(false);
            if (registered.Unregister(unregistered))
                await Task.Run(() => unregistered.WaitOne());
        }
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
        _log?.Invoke(new(name, moduleId, version, SafeHashPrefix(input.ReleaseIdentity.Sha256), failure, entries, total));
    }

    private static string SafeHashPrefix(string hash) => hash.Length >= 12 && hash.All(Uri.IsHexDigit) ? hash[..12].ToLowerInvariant() : "invalid";
    private static bool IsSafeFileName(string value) => !string.IsNullOrWhiteSpace(value) &&
        value == Path.GetFileName(value) && IsSafeSegment(value);
    private static bool IsSafeModuleId(string value) => !string.IsNullOrEmpty(value) && value.Length <= 128 &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) && !char.IsUpper(ch) || ch is '.' or '-' or '_');
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
        _lifetime.Cancel();
        try { await Task.WhenAll(_inflight.Values.Select(operation => operation.Task)); } catch { }
        _lifetime.Dispose();
        _parallelism.Dispose();
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
    private sealed record StagingMetadata(int FileCount, long TotalUncompressedBytes, IReadOnlyList<QmodStagedFile> Files);
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
            StringComparer.Ordinal.Equals(x.EnvironmentIdentity, y.EnvironmentIdentity);
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
    private sealed class PublicationLease(PublicationGate.GateLease local, Semaphore crossProcess) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            crossProcess.Release();
            crossProcess.Dispose();
            await local.DisposeAsync();
        }
    }
    private sealed class StagingOperation(CancellationTokenSource cancellation, Func<Task<QmodStagingResult>> factory) : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        private readonly Lazy<Task<QmodStagingResult>> _task = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        public Task<QmodStagingResult> Task => _task.Value;
        public void Dispose() => Cancellation.Dispose();
    }
}
