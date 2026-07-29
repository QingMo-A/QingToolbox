using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QingToolbox.Core.Updates;

public enum HostUpdateDownloadState { Idle, Downloading, Verifying, ReadyToInstall, Failed }

public sealed record HostUpdateDownloadProgress(HostUpdateDownloadState State, long BytesReceived, long ExpectedBytes,
    string? Error = null, string? InstallerPath = null, string? ReleaseVersion = null, long InstallerAssetId = 0);

public sealed record VerifiedHostInstaller(string Version, long InstallerAssetId, long ChecksumAssetId,
    string InstallerPath, long InstallerSize);

public sealed class HostUpdateInstallerDownloader(HttpClient client, string cacheRoot)
{
    public const long MaximumInstallerBytes = 512L * 1024 * 1024;
    public const long MaximumSidecarBytes = 4 * 1024;
    private static readonly Regex SidecarPattern = new("^(?<hash>[0-9A-Fa-f]{64})  (?<file>[^\\r\\n]+)\\r?\\n?$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> AllowedRedirectHosts = new(StringComparer.OrdinalIgnoreCase)
        { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };
    private readonly object _sync = new();
    private Task<HostUpdateDownloadProgress>? _inFlight;

    public event EventHandler<HostUpdateDownloadProgress>? ProgressChanged;

    public async Task<VerifiedHostInstaller?> GetVerifiedInstallerForHandoffAsync(HostReleaseInfo release, CancellationToken token)
    {
        var directory = ResolveControlledDirectory(release);
        var installer = ResolveControlledFile(directory, release.Installer.Name);
        var sidecar = ResolveControlledFile(directory, release.Checksum.Name);
        if (!release.Installer.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !await VerifyCachedAsync(release, installer, sidecar, token)) return null;
        return new(release.Version, release.Installer.Id, release.Checksum.Id, installer, release.Installer.Size);
    }

    public Task<HostUpdateDownloadProgress> DownloadAsync(HostReleaseInfo release, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_inFlight is { IsCompleted: false }) return _inFlight;
            _inFlight = DownloadCoreAsync(release, cancellationToken);
            return _inFlight;
        }
    }

    private async Task<HostUpdateDownloadProgress> DownloadCoreAsync(HostReleaseInfo release, CancellationToken cancellationToken)
    {
        HostUpdateDownloadProgress Emit(HostUpdateDownloadProgress value) =>
            Report(value with { ReleaseVersion = release.Version, InstallerAssetId = release.Installer.Id });
        var directory = ResolveControlledDirectory(release);
        var finalPath = ResolveControlledFile(directory, release.Installer.Name);
        var partPath = finalPath + ".part";
        var sidecarPath = ResolveControlledFile(directory, release.Checksum.Name);
        try
        {
            Directory.CreateDirectory(directory);
            if (await VerifyCachedAsync(release, finalPath, sidecarPath, cancellationToken))
                return Emit(new(HostUpdateDownloadState.ReadyToInstall, release.Installer.Size, release.Installer.Size, InstallerPath: finalPath));

            DeleteBestEffort(finalPath); DeleteBestEffort(partPath); DeleteBestEffort(sidecarPath);
            var sidecar = await DownloadSmallAsync(release.Checksum, cancellationToken);
            var expectedHash = ParseSidecar(sidecar, release.Installer.Name);
            await File.WriteAllBytesAsync(sidecarPath + ".part", sidecar, cancellationToken);

            Emit(new(HostUpdateDownloadState.Downloading, 0, release.Installer.Size));
            await DownloadInstallerAsync(release.Installer, partPath, bytes => Emit(new(HostUpdateDownloadState.Downloading, bytes, release.Installer.Size)), cancellationToken);
            Emit(new(HostUpdateDownloadState.Verifying, release.Installer.Size, release.Installer.Size));
            var actualHash = await HashAsync(partPath, cancellationToken);
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Installer SHA256 does not match the official sidecar.");
            File.Move(sidecarPath + ".part", sidecarPath, true);
            File.Move(partPath, finalPath, true);
            return Emit(new(HostUpdateDownloadState.ReadyToInstall, release.Installer.Size, release.Installer.Size, InstallerPath: finalPath));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeleteBestEffort(partPath); DeleteBestEffort(sidecarPath + ".part");
            return Emit(new(HostUpdateDownloadState.Idle, 0, release.Installer.Size));
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or TaskCanceledException)
        {
            DeleteBestEffort(partPath); DeleteBestEffort(sidecarPath + ".part"); DeleteBestEffort(finalPath); DeleteBestEffort(sidecarPath);
            return Emit(new(HostUpdateDownloadState.Failed, 0, release.Installer.Size, exception.Message));
        }
    }

    private async Task<bool> VerifyCachedAsync(HostReleaseInfo release, string installer, string sidecar, CancellationToken token)
    {
        if (!File.Exists(installer) || !File.Exists(sidecar)) return false;
        try
        {
            if (new FileInfo(installer).Length != release.Installer.Size || new FileInfo(sidecar).Length != release.Checksum.Size ||
                new FileInfo(sidecar).Length > MaximumSidecarBytes) return false;
            var expected = ParseSidecar(await File.ReadAllBytesAsync(sidecar, token), release.Installer.Name);
            return (await HashAsync(installer, token)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    private async Task<byte[]> DownloadSmallAsync(HostReleaseAssetIdentity asset, CancellationToken token)
    {
        if (asset.Size <= 0 || asset.Size > MaximumSidecarBytes) throw new InvalidDataException("Official checksum size is invalid.");
        using var response = await SendFollowingOfficialRedirectsAsync(asset.DownloadUri, token);
        ValidateLength(response, asset.Size);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream((int)asset.Size);
        await CopyBoundedAsync(input, output, asset.Size, null, token);
        return output.ToArray();
    }

    private async Task DownloadInstallerAsync(HostReleaseAssetIdentity asset, string path, Action<long> progress, CancellationToken token)
    {
        if (asset.Size <= 0 || asset.Size > MaximumInstallerBytes) throw new InvalidDataException("Official installer size is invalid.");
        using var response = await SendFollowingOfficialRedirectsAsync(asset.DownloadUri, token);
        ValidateLength(response, asset.Size);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await CopyBoundedAsync(input, output, asset.Size, progress, token);
        await output.FlushAsync(token);
    }

    private async Task<HttpResponseMessage> SendFollowingOfficialRedirectsAsync(Uri initial, CancellationToken token)
    {
        var uri = initial;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            ValidateDownloadUri(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/octet-stream");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new HttpRequestException("Official asset redirect is missing its location.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue;
            }
            response.EnsureSuccessStatusCode(); return response;
        }
        throw new HttpRequestException("Official asset redirected too many times.");
    }

    private static void ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !AllowedRedirectHosts.Contains(uri.Host))
            throw new HttpRequestException("Official asset redirected outside the allowed GitHub download boundary.");
    }

    private static void ValidateLength(HttpResponseMessage response, long expected)
    {
        if (response.Content.Headers.ContentLength is { } length && length != expected)
            throw new InvalidDataException("Download Content-Length does not match the official asset size.");
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long expected, Action<long>? progress, CancellationToken token)
    {
        var buffer = new byte[64 * 1024]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, token); if (read == 0) break;
            total += read; if (total > expected) throw new InvalidDataException("Download exceeded the official asset size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token); progress?.Invoke(total);
        }
        if (total != expected) throw new InvalidDataException("Download ended before the official asset size was received.");
    }

    public static string ParseSidecar(byte[] bytes, string installerName)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumSidecarBytes) throw new InvalidDataException("Official checksum sidecar size is invalid.");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var match = SidecarPattern.Match(text);
        if (!match.Success || !string.Equals(match.Groups["file"].Value, installerName, StringComparison.Ordinal) ||
            Path.GetFileName(match.Groups["file"].Value) != match.Groups["file"].Value)
            throw new InvalidDataException("Official checksum sidecar format or filename is invalid.");
        return match.Groups["hash"].Value.ToUpperInvariant();
    }

    private string ResolveControlledDirectory(HostReleaseInfo release)
    {
        var root = Path.GetFullPath(cacheRoot);
        var directory = Path.GetFullPath(Path.Combine(root, release.Version, $"{release.Installer.Id}-{release.Checksum.Id}"));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update cache path escaped its root.");
        return directory;
    }

    private static string ResolveControlledFile(string directory, string name)
    {
        if (Path.GetFileName(name) != name) throw new InvalidDataException("Update asset filename is invalid.");
        var path = Path.GetFullPath(Path.Combine(directory, name));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update asset path escaped its cache directory.");
        return path;
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
    private HostUpdateDownloadProgress Report(HostUpdateDownloadProgress value) { ProgressChanged?.Invoke(this, value); return value; }
    private static void DeleteBestEffort(string path) { try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
}
