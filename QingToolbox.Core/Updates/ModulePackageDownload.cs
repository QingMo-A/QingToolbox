using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace QingToolbox.Core.Updates;

public enum ModulePackageDownloadStatus
{
    NotDownloaded, ConfirmingMetadata, MetadataChanged, MetadataStale, Downloading,
    Verifying, Verified, AlreadyVerified, Cancelled, SizeMismatch, HashMismatch,
    SourceUnavailable, SourceInvalid, UntrustedRedirect, StorageUnavailable, Failed,
    DisabledByEnvironment
}

public sealed record ModulePackageDownloadRequest(
    string ModuleId, string LocalVersion, SemanticVersion TargetVersion, ModuleUpdatePackage Package);
public sealed record ModulePackageDownloadProgress(long BytesReceived, long ExpectedBytes)
{
    public double? Percentage => ExpectedBytes > 0 ? BytesReceived * 100d / ExpectedBytes : null;
}
public sealed record VerifiedModulePackage(
    string ModuleId, SemanticVersion Version, string FileName, string FilePath,
    long Size, string Sha256, DateTimeOffset VerifiedAt);
public sealed record ModulePackageDownloadResult(
    ModulePackageDownloadStatus Status, VerifiedModulePackage? VerifiedPackage = null,
    ModuleUpdateResult? LatestUpdateResult = null);

public sealed class ModulePackageTransportResponse(Stream content, long? contentLength,
    IReadOnlyList<string> contentEncodings, IDisposable? owner = null) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public long? ContentLength { get; } = contentLength;
    public IReadOnlyList<string> ContentEncodings { get; } = contentEncodings;
    public async ValueTask DisposeAsync() { await Content.DisposeAsync(); owner?.Dispose(); }
}

public interface IModulePackageTransport
{
    Task<ModulePackageTransportResponse> OpenReadAsync(ModuleUpdatePackage package, CancellationToken token);
}

public sealed class ModulePackageTransportException(ModulePackageDownloadStatus status, string message) : Exception(message)
{
    public ModulePackageDownloadStatus Status { get; } = status;
}

public sealed class OfficialModulePackageTransport(HttpClient client) : IModulePackageTransport
{
    private static readonly HashSet<string> AssetHosts = new(StringComparer.OrdinalIgnoreCase)
    { "release-assets.githubusercontent.com", "objects.githubusercontent.com" };
    private static readonly HashSet<HttpStatusCode> Redirects =
    [HttpStatusCode.MovedPermanently, HttpStatusCode.Found, HttpStatusCode.SeeOther,
     HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect];

    public async Task<ModulePackageTransportResponse> OpenReadAsync(ModuleUpdatePackage package, CancellationToken token)
    {
        ValidatePackage(package);
        var current = package.Url;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.AbsoluteUri };
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (Redirects.Contains(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (redirects >= 3 || location is null || !location.IsAbsoluteUri || !IsTrustedAsset(location) || !visited.Add(location.AbsoluteUri))
                    throw new ModulePackageTransportException(ModulePackageDownloadStatus.UntrustedRedirect, "Untrusted package redirect.");
                current = location;
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                response.Dispose();
                throw new ModulePackageTransportException(ModulePackageDownloadStatus.SourceUnavailable, "Package source did not return HTTP 200.");
            }
            var stream = await response.Content.ReadAsStreamAsync(token);
            return new ModulePackageTransportResponse(stream, response.Content.Headers.ContentLength,
                response.Content.Headers.ContentEncoding.ToArray(), response);
        }
    }

    public static void ValidatePackage(ModuleUpdatePackage package)
    {
        if (package.Size <= 0 || package.Size > ModulePackageDownloadCoordinator.MaximumModulePackageSize)
            throw new ModulePackageTransportException(ModulePackageDownloadStatus.SizeMismatch, "Package size is outside the allowed range.");
        if (!IsSafeFileName(package.FileName))
            throw new ModulePackageTransportException(ModulePackageDownloadStatus.SourceInvalid, "Unsafe package file name.");
        var uri = package.Url;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ModulePackageTransportException(ModulePackageDownloadStatus.SourceInvalid, "Untrusted package URL.");
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 6 || segments[0] != "QingMo-A" || segments[1] != "QingToolbox" ||
            segments[2] != "releases" || segments[3] != "download" ||
            segments[4].Equals("latest", StringComparison.OrdinalIgnoreCase) ||
            segments.Any(x => Uri.UnescapeDataString(x) is "." or "..") ||
            Uri.UnescapeDataString(segments[^1]) != package.FileName)
            throw new ModulePackageTransportException(ModulePackageDownloadStatus.SourceInvalid, "Invalid official Release Asset URL.");
        try { _ = Convert.FromHexString(package.Sha256); }
        catch (FormatException) { throw new ModulePackageTransportException(ModulePackageDownloadStatus.SourceInvalid, "Invalid SHA256."); }
        if (package.Sha256.Length != 64) throw new ModulePackageTransportException(ModulePackageDownloadStatus.SourceInvalid, "Invalid SHA256.");
    }

    private static bool IsTrustedAsset(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && AssetHosts.Contains(uri.Host);
    private static bool IsSafeFileName(string value) => value.Length > 5 && value == Path.GetFileName(value) &&
        !value.Any(ch => ch is '/' or '\\' or ':' || char.IsControl(ch)) &&
        !value.EndsWith(' ') && !value.EndsWith('.') && value.EndsWith(".qmod", StringComparison.Ordinal);
}

public sealed class ModulePackageDownloadCoordinator : IAsyncDisposable
{
    public const long MaximumModulePackageSize = 256L * 1024 * 1024;
    private readonly IModuleUpdateChecker _checker;
    private readonly IModulePackageTransport _transport;
    private readonly string _cacheRoot;
    private readonly string _verifiedRoot;
    private readonly TimeProvider _timeProvider;
    private readonly bool _disabled;
    private readonly SemaphoreSlim _parallelism = new(2, 2);
    private readonly ConcurrentDictionary<string, DownloadOperation> _inflight = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();

    public ModulePackageDownloadCoordinator(IModuleUpdateChecker checker, IModulePackageTransport transport,
        string cacheDirectory, TimeProvider timeProvider, bool disabledByEnvironment)
    {
        _checker = checker; _transport = transport; _timeProvider = timeProvider; _disabled = disabledByEnvironment;
        _cacheRoot = Path.GetFullPath(cacheDirectory);
        _verifiedRoot = Path.GetFullPath(Path.Combine(_cacheRoot, "ModuleUpdates", "Packages", "Verified"));
        if (Path.GetPathRoot(_verifiedRoot) == _verifiedRoot) throw new ArgumentException("Verified root cannot be a volume root.", nameof(cacheDirectory));
    }

    public Task<ModulePackageDownloadResult> DownloadAsync(ModulePackageDownloadRequest request,
        IProgress<ModulePackageDownloadProgress>? progress = null, CancellationToken token = default)
    {
        if (_disabled) return Task.FromResult(new ModulePackageDownloadResult(ModulePackageDownloadStatus.DisabledByEnvironment));
        var key = GetKey(request);
        DownloadOperation? candidate = null;
        candidate = new DownloadOperation(CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token),
            () => RunAndRemoveAsync(key, request, progress, candidate!));
        var operation = _inflight.GetOrAdd(key, candidate);
        if (!ReferenceEquals(operation, candidate)) candidate.Dispose();
        return operation.Task;
    }

    public void Cancel(ModulePackageDownloadRequest request)
    { if (_inflight.TryGetValue(GetKey(request), out var operation)) operation.Cancellation.Cancel(); }

    private async Task<ModulePackageDownloadResult> RunAndRemoveAsync(string key, ModulePackageDownloadRequest request,
        IProgress<ModulePackageDownloadProgress>? progress, DownloadOperation operation)
    {
        try { return await RunAsync(request, progress, operation.Cancellation.Token); }
        finally { _inflight.TryRemove(new KeyValuePair<string, DownloadOperation>(key, operation)); operation.Dispose(); }
    }

    private async Task<ModulePackageDownloadResult> RunAsync(ModulePackageDownloadRequest request,
        IProgress<ModulePackageDownloadProgress>? progress, CancellationToken token)
    {
        try
        {
            OfficialModulePackageTransport.ValidatePackage(request.Package);
            var latest = await _checker.CheckModuleAsync(new(request.ModuleId, request.LocalVersion), true, token);
            if (latest.IsFromStaleCache) return new(ModulePackageDownloadStatus.MetadataStale, LatestUpdateResult: latest);
            if (latest.Status is ModuleUpdateStatus.SourceUnavailable) return new(ModulePackageDownloadStatus.SourceUnavailable, LatestUpdateResult: latest);
            if (latest.Status is ModuleUpdateStatus.SourceInvalid) return new(ModulePackageDownloadStatus.SourceInvalid, LatestUpdateResult: latest);
            if (latest.Status != ModuleUpdateStatus.UpdateAvailable || latest.SelectedRelease is null ||
                latest.SelectedRelease.Version.CompareTo(request.TargetVersion) != 0 || !SamePackage(latest.SelectedRelease.Package, request.Package))
                return new(ModulePackageDownloadStatus.MetadataChanged, LatestUpdateResult: latest);

            var paths = PreparePaths(request);
            var existing = await VerifyExistingAsync(paths.Final, request, token);
            if (existing is not null) return new(ModulePackageDownloadStatus.AlreadyVerified, existing, latest);
            await _parallelism.WaitAsync(token);
            try
            {
                await using var response = await _transport.OpenReadAsync(request.Package, token);
                if (response.ContentEncodings.Any(x => !x.Equals("identity", StringComparison.OrdinalIgnoreCase)) ||
                    response.ContentLength is { } length && length != request.Package.Size)
                    return new(ModulePackageDownloadStatus.SizeMismatch, LatestUpdateResult: latest);
                var temp = Path.Combine(paths.Directory, $".{request.Package.FileName}.partial-{Guid.NewGuid():N}");
                try
                {
                    long received = 0; byte[] actualHash;
                    var lastProgressAt = Stopwatch.GetTimestamp(); var lastProgressPercent = -1L;
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
                        FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        var buffer = new byte[65536];
                        while (true)
                        {
                            var read = await response.Content.ReadAsync(buffer, token); if (read == 0) break;
                            received += read; if (received > request.Package.Size) return new(ModulePackageDownloadStatus.SizeMismatch, LatestUpdateResult: latest);
                            hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), token);
                            var percent = received * 100 / request.Package.Size;
                            if (received == request.Package.Size || percent > lastProgressPercent ||
                                Stopwatch.GetElapsedTime(lastProgressAt) >= TimeSpan.FromMilliseconds(100))
                            {
                                progress?.Report(new(received, request.Package.Size));
                                lastProgressAt = Stopwatch.GetTimestamp(); lastProgressPercent = percent;
                            }
                        }
                        await output.FlushAsync(token); actualHash = hash.GetHashAndReset();
                    }
                    if (received != request.Package.Size) return new(ModulePackageDownloadStatus.SizeMismatch, LatestUpdateResult: latest);
                    var expectedHash = Convert.FromHexString(request.Package.Sha256);
                    if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash)) return new(ModulePackageDownloadStatus.HashMismatch, LatestUpdateResult: latest);
                    EnsureSafeDirectory(paths.Directory); File.Move(temp, paths.Final, false); temp = string.Empty;
                    var verified = new VerifiedModulePackage(request.ModuleId, request.TargetVersion, request.Package.FileName,
                        paths.Final, received, Convert.ToHexString(actualHash).ToLowerInvariant(), _timeProvider.GetUtcNow());
                    await WriteRecordAsync(paths.Record, request, verified, token);
                    return new(ModulePackageDownloadStatus.Verified, verified, latest);
                }
                finally { TryDelete(temp); }
            }
            finally { _parallelism.Release(); }
        }
        catch (OperationCanceledException) { return new(ModulePackageDownloadStatus.Cancelled); }
        catch (ModulePackageTransportException ex) { return new(ex.Status); }
        catch (ModuleUpdateProtocolException) { return new(ModulePackageDownloadStatus.SourceInvalid); }
        catch (ModuleUpdateSourceUnavailableException) { return new(ModulePackageDownloadStatus.SourceUnavailable); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(ModulePackageDownloadStatus.StorageUnavailable); }
        catch { return new(ModulePackageDownloadStatus.Failed); }
    }

    private (string Directory, string Final, string Record) PreparePaths(ModulePackageDownloadRequest request)
    {
        if (!request.ModuleId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_'))
            throw new ModulePackageTransportException(ModulePackageDownloadStatus.SourceInvalid, "Unsafe module id.");
        var directory = Path.GetFullPath(Path.Combine(_verifiedRoot, request.ModuleId, request.TargetVersion.ToString(), request.Package.Sha256.ToLowerInvariant()));
        EnsureWithinRoot(directory); CreateSafeDirectoryChain(directory);
        var final = Path.GetFullPath(Path.Combine(directory, request.Package.FileName)); EnsureWithinRoot(final);
        if (File.Exists(final) && IsReparse(final)) throw new IOException("Reparse-point package file rejected.");
        return (directory, final, Path.Combine(directory, "package-record.json"));
    }

    private async Task<VerifiedModulePackage?> VerifyExistingAsync(string path, ModulePackageDownloadRequest request, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        if (IsReparse(path)) throw new IOException("Reparse-point package file rejected.");
        var info = new FileInfo(path);
        if (info.Length == request.Package.Size)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            var hash = await SHA256.HashDataAsync(stream, token);
            if (CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(request.Package.Sha256)))
                return new(request.ModuleId, request.TargetVersion, request.Package.FileName, path, info.Length,
                    Convert.ToHexString(hash).ToLowerInvariant(), _timeProvider.GetUtcNow());
        }
        var invalid = Path.Combine(Path.GetDirectoryName(path)!, $".invalid-{Guid.NewGuid():N}");
        File.Move(path, invalid); TryDelete(invalid); return null;
    }

    private async Task WriteRecordAsync(string path, ModulePackageDownloadRequest request, VerifiedModulePackage verified, CancellationToken token)
    {
        if (File.Exists(path) && IsReparse(path)) throw new IOException("Reparse-point package record rejected.");
        var temp = path + $".partial-{Guid.NewGuid():N}";
        try
        {
            var record = new { schemaVersion = 1, moduleId = request.ModuleId, localVersionAtDownload = request.LocalVersion,
                targetVersion = request.TargetVersion.ToString(), fileName = request.Package.FileName,
                expectedSize = request.Package.Size, sha256 = request.Package.Sha256.ToLowerInvariant(),
                officialPackageUrl = request.Package.Url.AbsoluteUri, verifiedAt = verified.VerifiedAt };
            await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(record), token);
            File.Move(temp, path, true);
        }
        finally { TryDelete(temp); }
    }

    private void CreateSafeDirectoryChain(string target)
    {
        foreach (var managed in new[] { _cacheRoot, Path.Combine(_cacheRoot, "ModuleUpdates"),
            Path.Combine(_cacheRoot, "ModuleUpdates", "Packages"), _verifiedRoot })
        { Directory.CreateDirectory(managed); if (IsReparse(managed)) throw new IOException("Reparse-point directory rejected."); }
        var current = _verifiedRoot;
        foreach (var part in Path.GetRelativePath(_verifiedRoot, target).Split(Path.DirectorySeparatorChar))
        { current = Path.Combine(current, part); Directory.CreateDirectory(current); EnsureSafeDirectory(current); }
    }
    private void EnsureSafeDirectory(string path) { EnsureWithinRoot(path); if (IsReparse(path)) throw new IOException("Reparse-point directory rejected."); }
    private void EnsureWithinRoot(string path)
    {
        var prefix = _verifiedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !Path.GetFullPath(path).Equals(_verifiedRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Package path escaped the verified root.");
    }
    private static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static bool SamePackage(ModuleUpdatePackage a, ModuleUpdatePackage b) => a.FileName == b.FileName &&
        a.Url.AbsoluteUri.Equals(b.Url.AbsoluteUri, StringComparison.Ordinal) &&
        a.Size == b.Size && a.Sha256.Equals(b.Sha256, StringComparison.OrdinalIgnoreCase);
    private static string GetKey(ModulePackageDownloadRequest request) => $"{request.ModuleId}\n{request.LocalVersion}\n{request.TargetVersion}\n{request.Package.Sha256}";
    private static void TryDelete(string? path) { if (string.IsNullOrEmpty(path)) return; try { File.Delete(path); } catch { } }
    public void Cancel() => _lifetime.Cancel();
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await Task.WhenAll(_inflight.Values.Select(value => value.Task)); } catch { }
        _lifetime.Dispose(); _parallelism.Dispose();
    }
    private sealed class DownloadOperation(CancellationTokenSource cancellation, Func<Task<ModulePackageDownloadResult>> factory) : IDisposable
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        private readonly Lazy<Task<ModulePackageDownloadResult>> _task = new(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        public Task<ModulePackageDownloadResult> Task => _task.Value;
        public void Dispose() => Cancellation.Dispose();
    }
}
