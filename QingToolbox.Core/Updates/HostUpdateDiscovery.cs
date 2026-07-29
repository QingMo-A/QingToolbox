using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace QingToolbox.Core.Updates;

public enum HostUpdateCheckState { Idle, Checking, UpToDate, UpdateAvailable, Failed }

public sealed record HostReleaseAssetIdentity(long Id, string Name, Uri DownloadUri, long Size);

public sealed record HostReleaseInfo(string Version, DateTimeOffset PublishedAt, string Summary,
    HostReleaseAssetIdentity Installer, HostReleaseAssetIdentity Checksum)
{
    public string InstallerFileName => Installer.Name;
    public string ChecksumFileName => Checksum.Name;
}

public sealed record HostUpdateCheckResult(HostUpdateCheckState State, string CurrentVersion,
    HostReleaseInfo? Release, DateTimeOffset? LastSuccessfulCheck, bool FromCache = false);

public sealed class HostUpdateDiscoveryService
{
    public static readonly Uri OfficialReleasesApi = new("https://api.github.com/repos/QingMo-A/QingToolbox/releases");
    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _client;
    private readonly string? _cachePath;
    private readonly SemanticVersion _currentVersion;
    private readonly TimeProvider _timeProvider;
    private readonly bool _networkEnabled;
    private readonly object _sync = new();
    private Task<HostUpdateCheckResult>? _inFlight;

    public HostUpdateDiscoveryService(HttpClient client, string? cachePath, SemanticVersion currentVersion,
        TimeProvider timeProvider, bool networkEnabled)
    {
        (_client, _cachePath, _currentVersion, _timeProvider, _networkEnabled) =
            (client, cachePath, currentVersion, timeProvider, networkEnabled);
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
            _client.DefaultRequestHeaders.UserAgent.ParseAdd($"QingToolbox/{currentVersion}");
    }

    public Task<HostUpdateCheckResult> CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight.WaitAsync(cancellationToken);
            _inFlight = CheckCoreAsync(manual, cancellationToken);
            return _inFlight;
        }
    }

    private async Task<HostUpdateCheckResult> CheckCoreAsync(bool manual, CancellationToken cancellationToken)
    {
        var cache = await ReadCacheAsync(cancellationToken);
        var now = _timeProvider.GetUtcNow();
        if (!_networkEnabled)
            return new(HostUpdateCheckState.Idle, _currentVersion.ToString(), null, cache?.LastSuccessfulCheck);
        if (!manual && cache?.LastSuccessfulCheck is { } checkedAt && now - checkedAt < AutomaticInterval)
        {
            try { return Select(cache.ReleasesJson, checkedAt, true); }
            catch (JsonException) { cache = null; }
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, OfficialReleasesApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (!string.IsNullOrWhiteSpace(cache?.ETag) && EntityTagHeaderValue.TryParse(cache.ETag, out var etag))
                request.Headers.IfNoneMatch.Add(etag);
            if (cache?.LastModified is { } modified) request.Headers.IfModifiedSince = modified;

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            string json;
            if (response.StatusCode == HttpStatusCode.NotModified && cache?.ReleasesJson is { Length: > 0 })
                json = cache.ReleasesJson;
            else
            {
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(cancellationToken);
                _ = ParseReleases(json);
            }

            var updated = new CacheRecord(now, response.Headers.ETag?.ToString() ?? cache?.ETag,
                response.Content.Headers.LastModified ?? response.Headers.Date ?? cache?.LastModified, json);
            await WriteCacheBestEffortAsync(updated, cancellationToken);
            return Select(json, now, response.StatusCode == HttpStatusCode.NotModified);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException)
        {
            return new(HostUpdateCheckState.Failed, _currentVersion.ToString(), null, cache?.LastSuccessfulCheck);
        }
    }

    private HostUpdateCheckResult Select(string json, DateTimeOffset checkedAt, bool fromCache)
    {
        var release = SelectBestRelease(ParseReleases(json), _currentVersion);
        return new(release is null ? HostUpdateCheckState.UpToDate : HostUpdateCheckState.UpdateAvailable,
            _currentVersion.ToString(), release, checkedAt, fromCache);
    }

    public static HostReleaseInfo? SelectBestRelease(IEnumerable<ReleaseRecord> releases, SemanticVersion current)
    {
        var currentChannel = Channel(current);
        return releases.Where(x => !x.Draft && TryTag(x.TagName, out _))
            .Select(x => (Record: x, Version: ParseTag(x.TagName)))
            .Where(x => x.Version.CompareTo(current) > 0 && IsChannelAllowed(currentChannel, Channel(x.Version)))
            .OrderByDescending(x => x.Version, Comparer<SemanticVersion>.Create((a, b) => a.CompareTo(b)))
            .Select(x => CreateRelease(x.Record, x.Version))
            .FirstOrDefault(x => x is not null);
    }

    private static HostReleaseInfo? CreateRelease(ReleaseRecord release, SemanticVersion version)
    {
        var versionText = version.ToString();
        var installer = $"QingToolbox-{versionText}-win-x64-setup.exe";
        var checksum = $"{installer}.sha256";
        var installers = release.Assets.Where(x => string.Equals(x.Name, installer, StringComparison.Ordinal)).ToArray();
        var checksums = release.Assets.Where(x => string.Equals(x.Name, checksum, StringComparison.Ordinal)).ToArray();
        if (installers.Length != 1 || checksums.Length != 1 || installers[0].Size > HostUpdateInstallerDownloader.MaximumInstallerBytes ||
            checksums[0].Size > HostUpdateInstallerDownloader.MaximumSidecarBytes ||
            !TryAsset(installers[0], out var installerAsset) || !TryAsset(checksums[0], out var checksumAsset))
            return null;
        return new(versionText, release.PublishedAt, Summarize(release.Body), installerAsset!, checksumAsset!);
    }

    private static bool TryAsset(ReleaseAssetRecord asset, out HostReleaseAssetIdentity? identity)
    {
        identity = null;
        if (asset.Id <= 0 || asset.Size <= 0 || !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        identity = new(asset.Id, asset.Name, uri, asset.Size);
        return true;
    }

    private static string Summarize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var plain = string.Join(' ', body.Replace("\r", "").Split('\n').Select(x => x.Trim())
            .Where(x => x.Length > 0)).Replace("#", "").Replace("*", "").Replace("`", "");
        return plain.Length <= 320 ? plain : plain[..317] + "…";
    }

    private static int Channel(SemanticVersion version)
    {
        if (!version.IsPrerelease) return 3;
        var label = version.Prerelease!.Split('.')[0];
        if (label.StartsWith("rc", StringComparison.OrdinalIgnoreCase)) return 2;
        if (label.StartsWith("beta", StringComparison.OrdinalIgnoreCase)) return 1;
        return 0;
    }

    private static bool IsChannelAllowed(int current, int candidate) => candidate >= current;
    private static bool TryTag(string tag, out SemanticVersion? version) => SemanticVersion.TryParse(NormalizeTag(tag), out version);
    private static SemanticVersion ParseTag(string tag) => SemanticVersion.Parse(NormalizeTag(tag));
    private static string NormalizeTag(string tag) => tag.Length > 0 && tag[0] is 'v' or 'V' ? tag[1..] : tag;

    public static IReadOnlyList<ReleaseRecord> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("Release response must be an array.");
        var result = new List<ReleaseRecord>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) continue;
            var assets = item.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array
                ? assetsElement.EnumerateArray().Select(x => new ReleaseAssetRecord(
                    x.TryGetProperty("id", out var id) && id.TryGetInt64(out var parsedId) ? parsedId : 0,
                    x.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                    x.TryGetProperty("browser_download_url", out var url) ? url.GetString() : null,
                    x.TryGetProperty("size", out var size) && size.TryGetInt64(out var parsedSize) ? parsedSize : 0)).ToArray() : [];
            var published = item.TryGetProperty("published_at", out var date) && date.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(date.GetString(), out var parsed) ? parsed : DateTimeOffset.MinValue;
            result.Add(new(tag.GetString()!, item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
                published, item.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null, assets));
        }
        return result;
    }

    private async Task<CacheRecord?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        if (_cachePath is null || !File.Exists(_cachePath)) return null;
        try { return JsonSerializer.Deserialize<CacheRecord>(await File.ReadAllTextAsync(_cachePath, cancellationToken), JsonOptions); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private async Task WriteCacheBestEffortAsync(CacheRecord cache, CancellationToken cancellationToken)
    {
        if (_cachePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            var temp = _cachePath + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(cache, JsonOptions), cancellationToken);
            File.Move(temp, _cachePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public sealed record ReleaseAssetRecord(long Id, string Name, string? BrowserDownloadUrl, long Size);
    public sealed record ReleaseRecord(string TagName, bool Draft, DateTimeOffset PublishedAt, string? Body,
        IReadOnlyList<ReleaseAssetRecord> Assets);
    private sealed record CacheRecord(DateTimeOffset LastSuccessfulCheck, string? ETag,
        DateTimeOffset? LastModified, string ReleasesJson);
}
