using QingToolbox.Abstractions.Modules;
using QingToolbox.ModuleLoader;

namespace QingToolbox.Shell.Services;

/// <summary>
/// Keeps manifest enumeration and I/O off the WPF dispatcher. The returned
/// immutable snapshot is applied to UI-bound registries by the caller.
/// </summary>
public sealed class ModuleDiscoveryCoordinator(
    ModuleManifestScanner scanner,
    ApplicationPaths applicationPaths)
{
    public Task<IReadOnlyList<DiscoveredModule>> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var scans = new List<DiscoveredModule>();
            foreach (var directory in applicationPaths.ModuleDiscoveryDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scans.AddRange(await scanner.ScanAsync(directory, cancellationToken).ConfigureAwait(false));
            }

            return (IReadOnlyList<DiscoveredModule>)scans
                .GroupBy(module => module.Manifest.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }, cancellationToken);
}
