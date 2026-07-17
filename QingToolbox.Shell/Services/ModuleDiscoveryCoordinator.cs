using QingToolbox.Abstractions.Modules;
using QingToolbox.ModuleLoader;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.Startup;
using System.IO;

namespace QingToolbox.Shell.Services;

/// <summary>
/// Keeps manifest enumeration and I/O off the WPF dispatcher. The returned
/// immutable snapshot is applied to UI-bound registries by the caller.
/// </summary>
public sealed class ModuleDiscoveryCoordinator(
    ModuleManifestScanner scanner,
    ApplicationPaths applicationPaths,
    UserSettingsService settingsService,
    ModuleStartupFingerprintService fingerprintService)
{
    public sealed record AuthorizationEvaluation(
        string ModuleId, bool AuthorizationPresent, bool FingerprintMatches, string? FailureDiagnostic);
    public sealed record Snapshot(
        IReadOnlyList<DiscoveredModule> Modules, UserSettings Settings,
        IReadOnlyDictionary<string, AuthorizationEvaluation> Authorizations);

    public Task<Snapshot> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var scans = new List<DiscoveredModule>();
            try
            {
                foreach (var directory in applicationPaths.ModuleDiscoveryDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scans.AddRange(await scanner.ScanAsync(directory, cancellationToken).ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new ModuleDiscoveryException("The module root could not be enumerated.", exception); }

            var modules = scans
                .GroupBy(module => module.Manifest.Id, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            var settings = await settingsService.ReadAsync(cancellationToken).ConfigureAwait(false);
            var configured = settings.StartupModules.ToDictionary(item => item.ModuleId, StringComparer.Ordinal);
            var evaluations = new Dictionary<string, AuthorizationEvaluation>(StringComparer.Ordinal);
            foreach (var module in modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!configured.TryGetValue(module.Manifest.Id, out var authorization))
                {
                    evaluations[module.Manifest.Id] = new(module.Manifest.Id, false, false, null);
                    continue;
                }
                try
                {
                    var matches = await fingerprintService.MatchesAsync(module, authorization, cancellationToken)
                        .ConfigureAwait(false);
                    evaluations[module.Manifest.Id] = new(module.Manifest.Id, true, matches,
                        matches ? null : "startup.moduleChanged");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    evaluations[module.Manifest.Id] = new(module.Manifest.Id, true, false,
                        $"startup.moduleUnavailable:{exception.GetType().Name}");
                }
            }
            return new Snapshot(modules, settings, evaluations);
        }, cancellationToken);
}
