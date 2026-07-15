using System.IO;
using System.Security.Cryptography;
using System.Text;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Settings;

namespace QingToolbox.Shell.Services;

public sealed class ModuleStartupFingerprintService
{
    public const int CurrentFingerprintVersion = 1;

    public async Task<StartupModuleAuthorization> CreateAuthorizationAsync(
        DiscoveredModule module, CancellationToken cancellationToken = default)
    {
        if (!module.IsValid) throw new InvalidOperationException("The module manifest is invalid.");
        var root = Path.GetFullPath(module.ModuleDirectory);
        EnsureNoReparsePoint(root);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var manifestPath = Path.GetFullPath(module.ManifestPath);
        EnsureInsideRoot(manifestPath, prefix);
        var entry = Path.GetFullPath(Path.Combine(root, module.Manifest.Entry));
        EnsureInsideRoot(entry, prefix);
        if (!File.Exists(manifestPath) || !File.Exists(entry))
            throw new FileNotFoundException("The manifest or entry assembly is missing.");

        var payload = await ComputePayloadAsync(root, cancellationToken);
        return new StartupModuleAuthorization
        {
            ModuleId = module.Manifest.Id,
            Version = module.Manifest.Version,
            ManifestSha256 = await HashFileAsync(manifestPath, cancellationToken),
            EntryAssemblySha256 = await HashFileAsync(entry, cancellationToken),
            FingerprintVersion = CurrentFingerprintVersion,
            PayloadSha256 = payload.Hash,
            PayloadFileCount = payload.FileCount,
            ActivateOnStartup = true
        };
    }

    public async Task<bool> MatchesAsync(DiscoveredModule module, StartupModuleAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        if (authorization.FingerprintVersion != CurrentFingerprintVersion ||
            string.IsNullOrWhiteSpace(authorization.PayloadSha256) || authorization.PayloadFileCount <= 0) return false;
        var current = await CreateAuthorizationAsync(module, cancellationToken);
        return current.ModuleId == authorization.ModuleId && current.Version == authorization.Version &&
               current.FingerprintVersion == authorization.FingerprintVersion &&
               current.ManifestSha256 == authorization.ManifestSha256.ToUpperInvariant() &&
               current.EntryAssemblySha256 == authorization.EntryAssemblySha256.ToUpperInvariant() &&
               current.PayloadSha256 == authorization.PayloadSha256.ToUpperInvariant() &&
               current.PayloadFileCount == authorization.PayloadFileCount;
    }

    public static async Task<(string Hash, int FileCount)> ComputePayloadAsync(
        string moduleDirectory, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(moduleDirectory);
        EnsureNoReparsePoint(root);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = new List<(string RelativePath, string FullPath)>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            EnsureNoReparsePoint(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var fullPath = Path.GetFullPath(path);
                EnsureInsideRoot(fullPath, prefix);
                EnsureNoReparsePoint(fullPath);
                if (Directory.Exists(fullPath)) pending.Push(fullPath);
                else if (File.Exists(fullPath))
                    files.Add((Path.GetRelativePath(root, fullPath).Replace('\\', '/'), fullPath));
                else throw new IOException("A module payload item disappeared during fingerprinting.");
            }
        }

        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file.FullPath);
            var contentHash = await HashFileAsync(file.FullPath, cancellationToken);
            var record = Encoding.UTF8.GetBytes($"{file.RelativePath}\0{info.Length}\0{contentHash}\n");
            aggregate.AppendData(record);
        }
        return (Convert.ToHexString(aggregate.GetHashAndReset()), files.Count);
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureNoReparsePoint(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static void EnsureInsideRoot(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A module payload path escapes its directory.");
    }

    private static void EnsureNoReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Module payload links and reparse points are not allowed.");
    }
}
