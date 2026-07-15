using System.Security.Cryptography;
using System.IO;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Settings;

namespace QingToolbox.Shell.Services;

public sealed class ModuleStartupFingerprintService
{
    public async Task<StartupModuleAuthorization> CreateAuthorizationAsync(
        DiscoveredModule module, CancellationToken cancellationToken = default)
    {
        if (!module.IsValid) throw new InvalidOperationException("The module manifest is invalid.");
        var root = Path.GetFullPath(module.ModuleDirectory);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var entry = Path.GetFullPath(Path.Combine(root, module.Manifest.Entry));
        if (!entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The module entry escapes its directory.");
        if (!File.Exists(module.ManifestPath) || !File.Exists(entry))
            throw new FileNotFoundException("The manifest or entry assembly is missing.");
        return new StartupModuleAuthorization
        {
            ModuleId = module.Manifest.Id,
            Version = module.Manifest.Version,
            ManifestSha256 = await HashFileAsync(module.ManifestPath, cancellationToken),
            EntryAssemblySha256 = await HashFileAsync(entry, cancellationToken),
            ActivateOnStartup = true
        };
    }

    public async Task<bool> MatchesAsync(DiscoveredModule module, StartupModuleAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var current = await CreateAuthorizationAsync(module, cancellationToken);
            return current.ModuleId == authorization.ModuleId && current.Version == authorization.Version &&
                   current.ManifestSha256 == authorization.ManifestSha256.ToUpperInvariant() &&
                   current.EntryAssemblySha256 == authorization.EntryAssemblySha256.ToUpperInvariant();
        }
        catch { return false; }
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }
}
