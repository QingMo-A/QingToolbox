using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace QingToolbox.Shell.WebShell;

public sealed class WebAssetIdentity
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public WebAssetIdentity(string assetRoot)
    {
        AssetRoot = Path.GetFullPath(assetRoot);
        var manifestPath = Path.Combine(AssetRoot, "qing-web-assets.json");
        if (!File.Exists(manifestPath)) throw new WebAssetIdentityException("AssetManifestMissing");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(manifestHash), Convert.FromHexString(WebAssetBuildInfo.ExpectedManifestSha256)))
            throw new WebAssetIdentityException("AssetManifestHostMismatch");
        var manifest = JsonSerializer.Deserialize<WebAssetManifest>(manifestBytes, JsonOptions)
            ?? throw new WebAssetIdentityException("AssetManifestInvalid");
        if (manifest.SchemaVersion != WebAssetBuildInfo.SchemaVersion || manifest.AssetBuildId != WebAssetBuildInfo.ExpectedAssetBuildId)
            throw new WebAssetIdentityException("AssetBuildIdentityMismatch");
        var allowed = new Dictionary<string, WebAssetFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.OutputFiles)
        {
            var relative = item.Path.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains("..")) throw new WebAssetIdentityException("AssetPathInvalid");
            var full = Path.GetFullPath(Path.Combine(AssetRoot, relative));
            if (!full.StartsWith(AssetRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) throw new WebAssetIdentityException("AssetFileMissing");
            var info = new FileInfo(full); var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
            if (info.Length != item.Size || !string.Equals(hash, item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new WebAssetIdentityException("AssetFileHashMismatch");
            if (!allowed.TryAdd(item.Path.Replace('\\', '/'), item)) throw new WebAssetIdentityException("AssetPathDuplicate");
        }
        var actual = Directory.EnumerateFiles(AssetRoot, "*", SearchOption.AllDirectories).Where(x => !string.Equals(x, manifestPath, StringComparison.OrdinalIgnoreCase)).Select(x => Path.GetRelativePath(AssetRoot, x).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(allowed.Keys)) throw new WebAssetIdentityException("AssetFileSetMismatch");
        AssetBuildId = manifest.AssetBuildId; ManifestSha256 = manifestHash; AllowedFiles = new ReadOnlyDictionary<string, WebAssetFile>(allowed);
    }
    public string AssetRoot { get; }
    public string AssetBuildId { get; }
    public string ManifestSha256 { get; }
    public IReadOnlyDictionary<string, WebAssetFile> AllowedFiles { get; }
    public bool TryResolve(string requestPath, out string fullPath)
    {
        var relative = Uri.UnescapeDataString(requestPath.TrimStart('/')); if (relative.Length == 0) relative = "index.html"; relative = relative.Replace('\\', '/');
        if (!AllowedFiles.ContainsKey(relative)) { fullPath = string.Empty; return false; }
        fullPath = Path.GetFullPath(Path.Combine(AssetRoot, relative.Replace('/', Path.DirectorySeparatorChar))); return true;
    }
}
public sealed class WebAssetIdentityException(string code) : Exception("The packaged Web asset identity is invalid.") { public string Code { get; } = code; }
