using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace QingToolbox.Shell.WebShell;

public sealed class VerifiedWebAsset
{
    private readonly byte[] _content;
    public VerifiedWebAsset(string relativePath, string contentType, byte[] content, long size, string sha256)
    { RelativePath = relativePath; ContentType = contentType; _content = content.ToArray(); Size = size; Sha256 = sha256; }
    public string RelativePath { get; }
    public string ContentType { get; }
    public ReadOnlyMemory<byte> Content => _content.ToArray();
    public long Size { get; }
    public string Sha256 { get; }
    public Stream OpenRead() => new MemoryStream(_content, 0, _content.Length, writable: false, publiclyVisible: false);
}

public sealed class WebAssetIdentity
{
    public const int MaximumFileCount = 256;
    public const long MaximumFileBytes = 8 * 1024 * 1024;
    public const long MaximumTotalBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, VerifiedWebAsset> _assets;

    public WebAssetIdentity(string assetRoot, string? trustedBoundary = null)
    {
        AssetRoot = Path.GetFullPath(assetRoot);
        ValidateBoundary(Path.GetFullPath(trustedBoundary ?? AssetRoot), AssetRoot);
        var manifestPath = Path.Combine(AssetRoot, "qing-web-assets.json");
        RejectReparse(manifestPath, "AssetManifestReparsePoint");
        if (!File.Exists(manifestPath)) throw new WebAssetIdentityException("AssetManifestMissing");
        var manifestBytes = File.ReadAllBytes(manifestPath);
        RejectReparse(manifestPath, "AssetManifestReparsePoint");
        var manifestHash = Hash(manifestBytes);
        if (!FixedHexEquals(manifestHash, WebAssetBuildInfo.ExpectedManifestSha256))
            throw new WebAssetIdentityException("AssetManifestHostMismatch");
        var manifest = JsonSerializer.Deserialize<WebAssetManifest>(manifestBytes, JsonOptions)
            ?? throw new WebAssetIdentityException("AssetManifestInvalid");
        if (manifest.SchemaVersion != WebAssetBuildInfo.SchemaVersion || manifest.AssetBuildId != WebAssetBuildInfo.ExpectedAssetBuildId)
            throw new WebAssetIdentityException("AssetBuildIdentityMismatch");
        ValidateLimits(manifest.OutputFiles.Count, 0, 0);

        var actualList = EnumerateTreeFiles(AssetRoot).Where(path => !string.Equals(path, manifestPath, StringComparison.OrdinalIgnoreCase))
            .Take(MaximumFileCount + 1).Select(path => Path.GetRelativePath(AssetRoot, path).Replace('\\', '/')).ToList();
        if (actualList.Count > MaximumFileCount) throw new WebAssetIdentityException("AssetFileCountLimitExceeded");
        var actualFiles = actualList.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assets = new Dictionary<string, VerifiedWebAsset>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var item in manifest.OutputFiles)
        {
            var key = NormalizeRelativePath(item.Path);
            var fullPath = Path.Combine(AssetRoot, key.Replace('/', Path.DirectorySeparatorChar));
            RejectReparse(fullPath, "AssetFileReparsePoint");
            if (!File.Exists(fullPath)) throw new WebAssetIdentityException("AssetFileMissing");
            var bytes = File.ReadAllBytes(fullPath);
            RejectReparse(fullPath, "AssetFileReparsePoint");
            ValidateLimits(manifest.OutputFiles.Count, bytes.LongLength, totalBytes);
            if (bytes.LongLength > MaximumTotalBytes - totalBytes) throw new WebAssetIdentityException("AssetTotalSizeLimitExceeded");
            totalBytes += bytes.LongLength;
            ValidateLimits(manifest.OutputFiles.Count, bytes.LongLength, totalBytes);
            var hash = Hash(bytes);
            if (bytes.LongLength != item.Size || !FixedHexEquals(hash, item.Sha256)) throw new WebAssetIdentityException("AssetFileHashMismatch");
            if (!assets.TryAdd(key, new(key, ContentType(key), bytes, bytes.LongLength, hash))) throw new WebAssetIdentityException("AssetPathDuplicate");
        }
        if (!actualFiles.SetEquals(assets.Keys)) throw new WebAssetIdentityException("AssetFileSetMismatch");
        AssetBuildId = manifest.AssetBuildId;
        ManifestSha256 = manifestHash;
        _assets = new ReadOnlyDictionary<string, VerifiedWebAsset>(assets);
    }

    public string AssetRoot { get; }
    public string AssetBuildId { get; }
    public string ManifestSha256 { get; }
    public IReadOnlyDictionary<string, VerifiedWebAsset> Assets => _assets;
    public bool TryResolve(string requestPath, out VerifiedWebAsset asset)
    {
        var relative = Uri.UnescapeDataString(requestPath.TrimStart('/')).Replace('\\', '/');
        if (relative.Length == 0) relative = "index.html";
        return _assets.TryGetValue(relative, out asset!);
    }

    private static IEnumerable<string> EnumerateTreeFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop(); RejectReparse(directory, "AssetDirectoryReparsePoint");
            foreach (var child in Directory.EnumerateDirectories(directory)) { RejectReparse(child, "AssetDirectoryReparsePoint"); pending.Push(child); }
            foreach (var file in Directory.EnumerateFiles(directory)) { RejectReparse(file, "AssetFileReparsePoint"); yield return file; }
        }
    }
    private static void ValidateBoundary(string boundary, string root)
    {
        var prefix = boundary.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!string.Equals(boundary, root, StringComparison.OrdinalIgnoreCase) && !root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new WebAssetIdentityException("AssetRootOutsideTrustedBoundary");
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
        {
            RejectReparse(current.FullName, "AssetRootReparsePoint");
            if (string.Equals(current.FullName.TrimEnd(Path.DirectorySeparatorChar), boundary.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
    }
    private static void RejectReparse(string path, string code)
    { if (File.Exists(path) || Directory.Exists(path)) RejectReparseAttributes(File.GetAttributes(path), code); }
    internal static void RejectReparseAttributes(FileAttributes attributes, string code)
    { if ((attributes & FileAttributes.ReparsePoint) != 0) throw new WebAssetIdentityException(code); }
    internal static void ValidateLimits(int count, long fileBytes, long totalBytes)
    {
        if (count > MaximumFileCount) throw new WebAssetIdentityException("AssetFileCountLimitExceeded");
        if (fileBytes > MaximumFileBytes) throw new WebAssetIdentityException("AssetFileSizeLimitExceeded");
        if (totalBytes > MaximumTotalBytes) throw new WebAssetIdentityException("AssetTotalSizeLimitExceeded");
    }
    private static string NormalizeRelativePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.Split('/').Any(part => part is "" or "." or "..")) throw new WebAssetIdentityException("AssetPathInvalid");
        return normalized;
    }
    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    { ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8", ".json" => "application/json; charset=utf-8", ".svg" => "image/svg+xml", _ => "application/octet-stream" };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool FixedHexEquals(string left, string right)
    { try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right)); } catch (FormatException) { return false; } }
}

public sealed class WebAssetIdentityException(string code) : Exception("The packaged Web asset identity is invalid.")
{ public string Code { get; } = code; }
