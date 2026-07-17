using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QingToolbox.Core.Updates;

public sealed record ModuleUpdateSourceResponse<T>(
    T Value, bool IsFromStaleCache, DateTimeOffset FetchedAt,
    bool CachePersistenceFailed = false);
public interface IModuleUpdateSource
{
    Task<ModuleUpdateSourceResponse<OfficialModuleIndex>> GetIndexAsync(bool manual, CancellationToken cancellationToken);
    Task<ModuleUpdateSourceResponse<ModuleUpdateManifest>> GetManifestAsync(string moduleId, string relativePath, bool manual, CancellationToken cancellationToken);
}

public sealed record ModuleUpdateCacheEnvelope(
    int SchemaVersion, string SourceUrl, string? ETag,
    DateTimeOffset? LastModified, DateTimeOffset FetchedAt, byte[] Payload);

public interface IModuleUpdateCache
{
    Task<ModuleUpdateCacheEnvelope?> ReadAsync(string key, string sourceUrl, int payloadLimit, CancellationToken token);
    Task WriteAsync(string key, ModuleUpdateCacheEnvelope envelope, CancellationToken token);
    bool IsFresh(ModuleUpdateCacheEnvelope entry);
}

public sealed class ModuleUpdateCache(string root, TimeProvider timeProvider) : IModuleUpdateCache
{
    private readonly SemaphoreSlim[] _writeStripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public string Root { get; } = root;
    public TimeSpan Freshness { get; init; } = TimeSpan.FromHours(24);
    public async Task<ModuleUpdateCacheEnvelope?> ReadAsync(string key, string sourceUrl, int payloadLimit, CancellationToken token)
    {
        var path = Path.Combine(Root, SafeName(key) + ".cache.json");
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > payloadLimit * 2L + 16 * 1024) return null;
            var envelope = JsonSerializer.Deserialize<ModuleUpdateCacheEnvelope>(await File.ReadAllBytesAsync(path, token));
            if (envelope is null || envelope.SchemaVersion != 1 || envelope.SourceUrl != sourceUrl ||
                envelope.Payload.Length > payloadLimit || envelope.FetchedAt > timeProvider.GetUtcNow()) return null;
            return envelope;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public async Task WriteAsync(string key, ModuleUpdateCacheEnvelope envelope, CancellationToken token)
    {
        var stripe = _writeStripes[(SafeName(key).GetHashCode(StringComparison.Ordinal) & int.MaxValue) % _writeStripes.Length];
        await stripe.WaitAsync(token);
        string? temp = null;
        try
        {
            Directory.CreateDirectory(Root); var path = Path.Combine(Root, SafeName(key) + ".cache.json");
            temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes, token); await stream.FlushAsync(token); }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (temp is not null) TryDelete(temp); stripe.Release(); }
    }
    public bool IsFresh(ModuleUpdateCacheEnvelope entry)
    {
        var age = timeProvider.GetUtcNow() - entry.FetchedAt;
        return age >= TimeSpan.Zero && age < Freshness;
    }
    private static string SafeName(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}

public sealed class OfficialModuleUpdateSource(
    HttpClient httpClient, IModuleUpdateCache? cache, TimeProvider timeProvider) : IModuleUpdateSource
{
    private static readonly Uri IndexUri = new(ModuleUpdateIdentity.OfficialIndexUrl);
    private static readonly Uri BaseUri = new(ModuleUpdateIdentity.OfficialModulesBaseUrl);
    public Task<ModuleUpdateSourceResponse<OfficialModuleIndex>> GetIndexAsync(bool manual, CancellationToken token) =>
        FetchAsync("index", IndexUri, 256 * 1024, manual, ModuleUpdateProtocolParser.ParseIndex, token);
    public Task<ModuleUpdateSourceResponse<ModuleUpdateManifest>> GetManifestAsync(string moduleId, string relativePath, bool manual, CancellationToken token)
    {
        var uri = new Uri(BaseUri, relativePath);
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsoluteUri.StartsWith(BaseUri.AbsoluteUri, StringComparison.Ordinal)) throw new ModuleUpdateProtocolException("Unsafe resolved update URL.");
        return FetchAsync("module:" + moduleId, uri, 128 * 1024, manual, bytes => ModuleUpdateProtocolParser.ParseUpdate(bytes, moduleId), token);
    }
    private async Task<ModuleUpdateSourceResponse<T>> FetchAsync<T>(string key, Uri uri, int limit, bool manual, Func<ReadOnlyMemory<byte>, T> parse, CancellationToken token)
    {
        var cached = cache is null ? null : await cache.ReadAsync(key, uri.AbsoluteUri, limit, token);
        if (!manual && cached is not null && cache!.IsFresh(cached))
            return new(parse(cached.Payload), false, cached.FetchedAt);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (cached?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
        if (cached?.LastModified is { } modified) request.Headers.IfModifiedSince = modified;
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (cached is null) throw new ModuleUpdateProtocolException("Received 304 without a valid cache entry.");
                var cachedValue = parse(cached.Payload); var refreshTime = timeProvider.GetUtcNow();
                var refreshed = cached with { FetchedAt = refreshTime,
                    ETag = response.Headers.ETag?.ToString() ?? cached.ETag,
                    LastModified = response.Content.Headers.LastModified ?? cached.LastModified };
                var refreshPersistenceFailed = cache is not null && !await TryWriteCacheAsync(key, refreshed, token);
                return new(cachedValue, false, refreshTime, refreshPersistenceFailed);
            }
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new ModuleUpdateProtocolException("Metadata redirects are not allowed.");
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { } final || final.Host != "raw.githubusercontent.com") throw new ModuleUpdateProtocolException("Update metadata redirected to an untrusted host.");
            var payload = await ReadLimitedAsync(response.Content, limit, token); var value = parse(payload);
            var now = timeProvider.GetUtcNow();
            var persistenceFailed = cache is not null && !await TryWriteCacheAsync(key, new(1, uri.AbsoluteUri,
                response.Headers.ETag?.ToString(), response.Content.Headers.LastModified, now, payload), token);
            return new(value, false, now, persistenceFailed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ModuleUpdateProtocolException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cached is not null) return new(parse(cached.Payload), true, cached.FetchedAt);
            throw new ModuleUpdateSourceUnavailableException(ex.Message, ex);
        }
    }
    private async Task<bool> TryWriteCacheAsync(string key, ModuleUpdateCacheEnvelope envelope, CancellationToken token)
    {
        try { await cache!.WriteAsync(key, envelope, token); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("Module update cache persistence failed.");
            return false;
        }
    }
    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) throw new ModuleUpdateProtocolException("Response exceeds protocol size limit.");
        await using var stream = await content.ReadAsStreamAsync(token); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var read = await stream.ReadAsync(buffer, token); if (read == 0) break; if (output.Length + read > limit) throw new ModuleUpdateProtocolException("Response exceeds protocol size limit."); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
}

public sealed class ModuleUpdateSourceUnavailableException(string message, Exception inner) : Exception(message, inner);

public sealed class ModuleUpdateChecker(
    IModuleUpdateSource source, ModuleUpdateCompatibilityEvaluator evaluator,
    TimeProvider timeProvider, bool disabledByEnvironment)
{
    private readonly SemaphoreSlim _parallelism = new(3, 3);
    public async Task<IReadOnlyDictionary<string, ModuleUpdateResult>> CheckAllInstalledModulesAsync(
        IReadOnlyCollection<InstalledModuleVersion> installed, bool manual, CancellationToken token)
    {
        if (installed.Count == 0) return new Dictionary<string, ModuleUpdateResult>(StringComparer.Ordinal);
        if (disabledByEnvironment) return installed.ToDictionary(x => x.ModuleId, x => new ModuleUpdateResult(x.ModuleId, ModuleUpdateStatus.DisabledByEnvironment), StringComparer.Ordinal);
        ModuleUpdateSourceResponse<OfficialModuleIndex> index;
        try { index = await source.GetIndexAsync(manual, token); }
        catch (ModuleUpdateProtocolException) { return All(installed, ModuleUpdateStatus.SourceInvalid); }
        catch (ModuleUpdateSourceUnavailableException) { return All(installed, ModuleUpdateStatus.SourceUnavailable); }
        var tasks = installed.Select(item => index.Value.Modules.TryGetValue(item.ModuleId, out var path)
            ? RunMappedAsync(item, path, manual, index.IsFromStaleCache, token)
            : Task.FromResult(new ModuleUpdateResult(item.ModuleId, ModuleUpdateStatus.NotOfficial,
                IsFromStaleCache: index.IsFromStaleCache, CheckedAt: timeProvider.GetUtcNow())));
        var results = await Task.WhenAll(tasks); return results.ToDictionary(x => x.ModuleId, StringComparer.Ordinal);
    }
    public async Task<ModuleUpdateResult> CheckModuleAsync(InstalledModuleVersion module, bool manual, CancellationToken token)
    {
        if (disabledByEnvironment) return new(module.ModuleId, ModuleUpdateStatus.DisabledByEnvironment);
        try
        {
            var index = await source.GetIndexAsync(manual, token);
            return index.Value.Modules.TryGetValue(module.ModuleId, out var path)
                ? await RunMappedAsync(module, path, manual, index.IsFromStaleCache, token)
                : new(module.ModuleId, ModuleUpdateStatus.NotOfficial,
                    IsFromStaleCache: index.IsFromStaleCache, CheckedAt: timeProvider.GetUtcNow());
        }
        catch (ModuleUpdateProtocolException) { return new(module.ModuleId, ModuleUpdateStatus.SourceInvalid); }
        catch (ModuleUpdateSourceUnavailableException) { return new(module.ModuleId, ModuleUpdateStatus.SourceUnavailable); }
    }
    private async Task<ModuleUpdateResult> RunMappedAsync(InstalledModuleVersion module, string path, bool manual, bool indexIsStale, CancellationToken token)
    {
        await _parallelism.WaitAsync(token);
        try
        {
            var response = await source.GetManifestAsync(module.ModuleId, path, manual, token);
            var result = evaluator.Evaluate(module.ModuleId, module.Version, response.Value, timeProvider.GetUtcNow());
            return result with { IsFromStaleCache = indexIsStale || response.IsFromStaleCache };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ModuleUpdateProtocolException) { return new(module.ModuleId, ModuleUpdateStatus.SourceInvalid); }
        catch (ModuleUpdateSourceUnavailableException) { return new(module.ModuleId, ModuleUpdateStatus.SourceUnavailable); }
        finally { _parallelism.Release(); }
    }
    private static IReadOnlyDictionary<string, ModuleUpdateResult> All(IEnumerable<InstalledModuleVersion> modules, ModuleUpdateStatus status) =>
        modules.ToDictionary(x => x.ModuleId, x => new ModuleUpdateResult(x.ModuleId, status), StringComparer.Ordinal);
}

public sealed class ModuleUpdateCheckCoordinator(
    ModuleUpdateChecker checker, TimeProvider timeProvider) : IAsyncDisposable
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _manualPending;

    public async Task<ModuleUpdateBatchResult> CheckAsync(ModuleUpdateCheckRequest request, CancellationToken callerToken = default)
    {
        if (request.Modules.Count == 0) return new(new Dictionary<string, VersionBoundModuleUpdateResult>(), false,
            timeProvider.GetUtcNow(), ModuleUpdateBatchDisposition.NoModules);
        if (request.IsManual && Interlocked.Exchange(ref _manualPending, 1) != 0)
            return new(new Dictionary<string, VersionBoundModuleUpdateResult>(), false,
                timeProvider.GetUtcNow(), ModuleUpdateBatchDisposition.DuplicateSuppressed);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, _lifetime.Token);
        await _operationGate.WaitAsync(linked.Token);
        try
        {
            var snapshot = request.Modules.Select(x => new InstalledModuleVersion(x.ModuleId, x.Version)).ToArray();
            var raw = await checker.CheckAllInstalledModulesAsync(snapshot, request.IsManual, linked.Token);
            var bound = snapshot.ToDictionary(x => x.ModuleId,
                x => new VersionBoundModuleUpdateResult(x.ModuleId, x.Version, raw[x.ModuleId]), StringComparer.Ordinal);
            return new(bound, bound.Values.Any(x => x.Result.IsFromStaleCache), timeProvider.GetUtcNow());
        }
        finally
        {
            if (request.IsManual) Volatile.Write(ref _manualPending, 0);
            _operationGate.Release();
        }
    }

    public void Cancel() => _lifetime.Cancel();
    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel(); _lifetime.Dispose(); _operationGate.Dispose(); return ValueTask.CompletedTask;
    }
}
