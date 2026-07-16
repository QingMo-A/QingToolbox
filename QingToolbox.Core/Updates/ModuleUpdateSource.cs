using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QingToolbox.Core.Updates;

public sealed record ModuleUpdateSourceResponse<T>(T Value, bool IsFromStaleCache, DateTimeOffset FetchedAt);
public interface IModuleUpdateSource
{
    Task<ModuleUpdateSourceResponse<OfficialModuleIndex>> GetIndexAsync(bool manual, CancellationToken cancellationToken);
    Task<ModuleUpdateSourceResponse<ModuleUpdateManifest>> GetManifestAsync(string moduleId, string relativePath, bool manual, CancellationToken cancellationToken);
}

public sealed record CacheMetadata(string SourceUrl, string? ETag, DateTimeOffset? LastModified, DateTimeOffset FetchedAt);
public sealed record CacheEntry(byte[] Payload, CacheMetadata Metadata);

public sealed class ModuleUpdateCache(string root, TimeProvider timeProvider)
{
    public string Root { get; } = root;
    public TimeSpan Freshness { get; init; } = TimeSpan.FromHours(24);
    public async Task<CacheEntry?> ReadAsync(string key, string sourceUrl, CancellationToken token)
    {
        var name = SafeName(key); var payloadPath = Path.Combine(Root, name + ".json"); var metadataPath = payloadPath + ".meta";
        try
        {
            if (!File.Exists(payloadPath) || !File.Exists(metadataPath)) return null;
            var metadata = JsonSerializer.Deserialize<CacheMetadata>(await File.ReadAllTextAsync(metadataPath, token));
            if (metadata?.SourceUrl != sourceUrl) return null;
            return new(await File.ReadAllBytesAsync(payloadPath, token), metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public async Task WriteAsync(string key, byte[] payload, CacheMetadata metadata, CancellationToken token)
    {
        Directory.CreateDirectory(Root);
        var name = SafeName(key); var payloadPath = Path.Combine(Root, name + ".json"); var metadataPath = payloadPath + ".meta";
        var payloadTemp = payloadPath + ".tmp-" + Guid.NewGuid().ToString("N"); var metadataTemp = metadataPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(payloadTemp, payload, token);
            await File.WriteAllTextAsync(metadataTemp, JsonSerializer.Serialize(metadata), token);
            File.Move(payloadTemp, payloadPath, true); File.Move(metadataTemp, metadataPath, true);
        }
        finally { TryDelete(payloadTemp); TryDelete(metadataTemp); }
    }
    public bool IsFresh(CacheEntry entry) => timeProvider.GetUtcNow() - entry.Metadata.FetchedAt < Freshness;
    private static string SafeName(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }
}

public sealed class OfficialModuleUpdateSource(
    HttpClient httpClient, ModuleUpdateCache? cache, TimeProvider timeProvider) : IModuleUpdateSource
{
    private static readonly Uri IndexUri = new(ModuleUpdateIdentity.OfficialIndexUrl);
    private static readonly Uri BaseUri = new(ModuleUpdateIdentity.OfficialModulesBaseUrl);
    public Task<ModuleUpdateSourceResponse<OfficialModuleIndex>> GetIndexAsync(bool manual, CancellationToken token) =>
        FetchAsync("index", IndexUri, 256 * 1024, manual, ModuleUpdateProtocolParser.ParseIndex, token);
    public Task<ModuleUpdateSourceResponse<ModuleUpdateManifest>> GetManifestAsync(string moduleId, string relativePath, bool manual, CancellationToken token)
    {
        var uri = new Uri(BaseUri, relativePath);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "raw.githubusercontent.com" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.AbsoluteUri.StartsWith(BaseUri.AbsoluteUri, StringComparison.Ordinal)) throw new ModuleUpdateProtocolException("Unsafe resolved update URL.");
        return FetchAsync("module:" + moduleId, uri, 128 * 1024, manual, bytes => ModuleUpdateProtocolParser.ParseUpdate(bytes, moduleId), token);
    }
    private async Task<ModuleUpdateSourceResponse<T>> FetchAsync<T>(string key, Uri uri, int limit, bool manual, Func<ReadOnlyMemory<byte>, T> parse, CancellationToken token)
    {
        var cached = cache is null ? null : await cache.ReadAsync(key, uri.AbsoluteUri, token);
        if (!manual && cached is not null && cache!.IsFresh(cached))
            return new(parse(cached.Payload), false, cached.Metadata.FetchedAt);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (cached?.Metadata.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out var tag)) request.Headers.IfNoneMatch.Add(tag);
        if (cached?.Metadata.LastModified is { } modified) request.Headers.IfModifiedSince = modified;
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (cached is null) throw new ModuleUpdateProtocolException("Received 304 without a valid cache entry.");
                return new(parse(cached.Payload), false, cached.Metadata.FetchedAt);
            }
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { } final || final.Host != "raw.githubusercontent.com") throw new ModuleUpdateProtocolException("Update metadata redirected to an untrusted host.");
            var payload = await ReadLimitedAsync(response.Content, limit, token); var value = parse(payload);
            var now = timeProvider.GetUtcNow();
            if (cache is not null) await cache.WriteAsync(key, payload, new(uri.AbsoluteUri,
                response.Headers.ETag?.ToString(), response.Content.Headers.LastModified, now), token);
            return new(value, false, now);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ModuleUpdateProtocolException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cached is not null) return new(parse(cached.Payload), true, cached.Metadata.FetchedAt);
            throw new ModuleUpdateSourceUnavailableException(ex.Message, ex);
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
    private readonly Dictionary<string, Task<ModuleUpdateResult>> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public async Task<IReadOnlyDictionary<string, ModuleUpdateResult>> CheckAllInstalledModulesAsync(
        IReadOnlyCollection<InstalledModuleVersion> installed, bool manual, CancellationToken token)
    {
        if (disabledByEnvironment) return installed.ToDictionary(x => x.ModuleId, x => new ModuleUpdateResult(x.ModuleId, ModuleUpdateStatus.DisabledByEnvironment), StringComparer.Ordinal);
        ModuleUpdateSourceResponse<OfficialModuleIndex> index;
        try { index = await source.GetIndexAsync(manual, token); }
        catch (ModuleUpdateProtocolException) { return All(installed, ModuleUpdateStatus.SourceInvalid); }
        catch (ModuleUpdateSourceUnavailableException) { return All(installed, ModuleUpdateStatus.SourceUnavailable); }
        var tasks = installed.Select(item => index.Value.Modules.TryGetValue(item.ModuleId, out var path)
            ? CheckMappedAsync(item, path, manual, token)
            : Task.FromResult(new ModuleUpdateResult(item.ModuleId, ModuleUpdateStatus.NotOfficial, CheckedAt: timeProvider.GetUtcNow())));
        var results = await Task.WhenAll(tasks); return results.ToDictionary(x => x.ModuleId, StringComparer.Ordinal);
    }
    public async Task<ModuleUpdateResult> CheckModuleAsync(InstalledModuleVersion module, bool manual, CancellationToken token)
    {
        if (disabledByEnvironment) return new(module.ModuleId, ModuleUpdateStatus.DisabledByEnvironment);
        try
        {
            var index = await source.GetIndexAsync(manual, token);
            return index.Value.Modules.TryGetValue(module.ModuleId, out var path)
                ? await CheckMappedAsync(module, path, manual, token)
                : new(module.ModuleId, ModuleUpdateStatus.NotOfficial, CheckedAt: timeProvider.GetUtcNow());
        }
        catch (ModuleUpdateProtocolException) { return new(module.ModuleId, ModuleUpdateStatus.SourceInvalid); }
        catch (ModuleUpdateSourceUnavailableException) { return new(module.ModuleId, ModuleUpdateStatus.SourceUnavailable); }
    }
    private Task<ModuleUpdateResult> CheckMappedAsync(InstalledModuleVersion module, string path, bool manual, CancellationToken token)
    {
        lock (_gate)
        {
            if (_inflight.TryGetValue(module.ModuleId, out var existing)) return existing;
            var task = RunMappedAsync(module, path, manual, token);
            _inflight[module.ModuleId] = task; _ = task.ContinueWith(_ => { lock (_gate) _inflight.Remove(module.ModuleId); }, TaskScheduler.Default);
            return task;
        }
    }
    private async Task<ModuleUpdateResult> RunMappedAsync(InstalledModuleVersion module, string path, bool manual, CancellationToken token)
    {
        await _parallelism.WaitAsync(token);
        try
        {
            var response = await source.GetManifestAsync(module.ModuleId, path, manual, token);
            var result = evaluator.Evaluate(module.ModuleId, module.Version, response.Value, timeProvider.GetUtcNow());
            return result with { IsFromStaleCache = response.IsFromStaleCache };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (ModuleUpdateProtocolException) { return new(module.ModuleId, ModuleUpdateStatus.SourceInvalid); }
        catch (ModuleUpdateSourceUnavailableException) { return new(module.ModuleId, ModuleUpdateStatus.SourceUnavailable); }
        finally { _parallelism.Release(); }
    }
    private static IReadOnlyDictionary<string, ModuleUpdateResult> All(IEnumerable<InstalledModuleVersion> modules, ModuleUpdateStatus status) =>
        modules.ToDictionary(x => x.ModuleId, x => new ModuleUpdateResult(x.ModuleId, status), StringComparer.Ordinal);
}
