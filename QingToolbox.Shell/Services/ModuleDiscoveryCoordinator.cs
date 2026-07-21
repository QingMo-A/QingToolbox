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
    ModuleStartupFingerprintService fingerprintService,
    IModuleExecutionReadinessGate executionGate,
    ModuleTransactionRecoveryGate maintenanceGate)
{
    public sealed record AuthorizationEvaluation(
        string ModuleId, bool AuthorizationPresent, bool FingerprintMatches, string? FailureDiagnostic);
    public sealed record Snapshot(
        IReadOnlyList<DiscoveredModule> Modules, UserSettings Settings,
        IReadOnlyDictionary<string, AuthorizationEvaluation> Authorizations,
        long Generation);

    private readonly object _sync = new();
    private Task<Snapshot>? _activeRun;
    private long _generation;
    public bool IsRunning { get { lock (_sync) return _activeRun is { IsCompleted: false }; } }

    public async Task<Snapshot> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        await executionGate.WaitForRecoveryInspectionAsync(cancellationToken).ConfigureAwait(false);
        Task<Snapshot> run;
        lock (_sync)
        {
            if (_activeRun is null || _activeRun.IsCompleted)
                _activeRun = RunCoreAsync(Interlocked.Increment(ref _generation), cancellationToken);
            run = _activeRun;
        }
        return await run.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<Snapshot> RunCoreAsync(long generation, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            await using var maintenance = await maintenanceGate.EnterDiscoveryAsync(cancellationToken)
                .ConfigureAwait(false);
            var scans = new List<DiscoveredModule>();
            try
            {
                foreach (var directory in applicationPaths.ModuleDiscoveryDirectories)
                {
                    scans.AddRange(await scanner.ScanAsync(directory, cancellationToken).ConfigureAwait(false));
                }
            }
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
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    evaluations[module.Manifest.Id] = new(module.Manifest.Id, true, false,
                        $"startup.moduleUnavailable:{exception.GetType().Name}");
                }
            }
            return new Snapshot(modules, settings, evaluations, generation);
        }, cancellationToken);
}
