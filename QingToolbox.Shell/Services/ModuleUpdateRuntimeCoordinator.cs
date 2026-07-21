using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Core.Localization;
using QingToolbox.Core.Runtime;
using QingToolbox.Core.Settings;
using QingToolbox.Core.Updates;
using QingToolbox.ModuleLoader;

namespace QingToolbox.Shell.Services;

public sealed record ModuleUpdateRuntimeDiagnostic(
    string ModuleId,
    string Version,
    string ProgramDirectoryIdentity,
    string PayloadFingerprint,
    long LoadContextGeneration,
    string? RuntimeAssemblyInformationalVersion,
    bool IsLoaded,
    bool LoadContextCollected);

public sealed record ModuleUpdateRuntimeLogEvent(
    string EventName,
    string ModuleId,
    string Version,
    string State,
    string FailureCode,
    long LoadContextGeneration);

/// <summary>
/// Bridges the transaction contract to the one real Shell runtime and window
/// manager. It never mutates startup authorization and never records a full
/// module path.
/// </summary>
public sealed class ModuleUpdateRuntimeCoordinator : IModuleUpdateRuntimeCoordinator
{
    private static readonly TimeSpan WindowOperationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan UnloadVerificationTimeout = TimeSpan.FromSeconds(5);

    private readonly ModuleRuntimeManager _runtimeManager;
    private readonly ModuleWindowManager _windowManager;
    private readonly UserSettingsService _settingsService;
    private readonly ModuleManifestReader _manifestReader;
    private readonly ModuleManifestValidator _manifestValidator;
    private readonly string _userModulesRoot;
    private readonly string _moduleDataRoot;
    private readonly Action<DiscoveredModule>? _registerLocalization;
    private readonly Action<ModuleUpdateRuntimeLogEvent>? _log;
    private readonly ConcurrentDictionary<string, ModuleUpdateRuntimeDiagnostic> _diagnostics =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ModuleUpdateRuntimeState> _deferredRuntimeIntents =
        new(StringComparer.Ordinal);
    private int _deferRuntimeRestore;

    public ModuleUpdateRuntimeCoordinator(
        ModuleRuntimeManager runtimeManager,
        ModuleWindowManager windowManager,
        UserSettingsService settingsService,
        ModuleManifestReader manifestReader,
        ModuleManifestValidator manifestValidator,
        ModuleStartupFingerprintService fingerprintService,
        ApplicationPaths paths,
        LocalizationManager localization,
        SessionLogService sessionLog)
        : this(
            runtimeManager,
            windowManager,
            settingsService,
            manifestReader,
            manifestValidator,
            fingerprintService,
            paths.UserModulesDirectory,
            paths.ModuleDataDirectory,
            module => localization.RegisterModuleLocalization(
                module.Manifest.Id,
                module.ModuleDirectory,
                module.Manifest.Localization,
                module.Manifest.DefaultLanguage),
            entry =>
            {
                var message = $"{entry.EventName}; module={entry.ModuleId}; version={entry.Version}; " +
                              $"state={entry.State}; failure={entry.FailureCode}; " +
                              $"generation={entry.LoadContextGeneration}.";
                if (entry.FailureCode == "None")
                {
                    sessionLog.Information("ModuleUpdateRuntime", message);
                }
                else
                {
                    sessionLog.Warning("ModuleUpdateRuntime", message);
                }
            })
    {
    }

    internal ModuleUpdateRuntimeCoordinator(
        ModuleRuntimeManager runtimeManager,
        ModuleWindowManager windowManager,
        UserSettingsService settingsService,
        ModuleManifestReader manifestReader,
        ModuleManifestValidator manifestValidator,
        ModuleStartupFingerprintService fingerprintService,
        string userModulesRoot,
        string moduleDataRoot,
        Action<DiscoveredModule>? registerLocalization,
        Action<ModuleUpdateRuntimeLogEvent>? log)
    {
        _runtimeManager = runtimeManager;
        _windowManager = windowManager;
        _settingsService = settingsService;
        _manifestReader = manifestReader;
        _manifestValidator = manifestValidator;
        _ = fingerprintService;
        _userModulesRoot = NormalizeRoot(userModulesRoot, nameof(userModulesRoot));
        _moduleDataRoot = NormalizeRoot(moduleDataRoot, nameof(moduleDataRoot));
        _registerLocalization = registerLocalization;
        _log = log;
    }

    public ModuleUpdateRuntimeDiagnostic? GetDiagnostic(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        return _diagnostics.GetValueOrDefault(moduleId);
    }

    internal IDisposable BeginStartupRecoveryDeferral()
    {
        if (Interlocked.Exchange(ref _deferRuntimeRestore, 1) != 0)
        {
            throw new InvalidOperationException("Runtime restore deferral is already active.");
        }

        return new RestoreDeferralScope(this);
    }

    internal IReadOnlyDictionary<string, ModuleUpdateRuntimeState> DrainDeferredRuntimeIntents()
    {
        if (Volatile.Read(ref _deferRuntimeRestore) != 0)
        {
            throw new InvalidOperationException("Runtime restore deferral is still active.");
        }

        var drained = new Dictionary<string, ModuleUpdateRuntimeState>(StringComparer.Ordinal);
        foreach (var pair in _deferredRuntimeIntents.ToArray())
        {
            if (_deferredRuntimeIntents.TryRemove(pair.Key, out var intent))
            {
                drained.Add(pair.Key, intent);
            }
        }

        return drained;
    }

    public async Task<ModuleUpdateRuntimeState> GetRuntimeStateAsync(
        string moduleId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        var runtime = await _runtimeManager.GetSnapshotAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);
        var hasWindows = await _windowManager.IsWindowOpenAsync(moduleId, cancellationToken)
            .ConfigureAwait(false);
        var settings = await _settingsService.ReadAsync(cancellationToken).ConfigureAwait(false);
        var hasAuthorization = settings.StartupModules.Any(
            authorization => string.Equals(authorization.ModuleId, moduleId, StringComparison.Ordinal));

        if (runtime?.HasRuntimeRegistration == true)
        {
            await CaptureDiagnosticAsync(runtime, null, cancellationToken).ConfigureAwait(false);
        }

        Log("Runtime state captured", moduleId, runtime?.Version, runtime?.State.ToString(),
            "None", runtime?.LoadContextGeneration ?? 0);
        return new(
            hasWindows,
            runtime?.IsActive == true,
            runtime?.HasRuntimeRegistration == true,
            hasAuthorization);
    }

    public async Task<bool> RequestCloseWindowsAsync(
        string moduleId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        var closed = await _windowManager.CloseWindowAsync(
                moduleId, WindowOperationTimeout, cancellationToken)
            .ConfigureAwait(false);
        Log("Module windows close requested", moduleId, null,
            closed ? "Closed" : "Open", closed ? "None" : "WindowCloseFailed", 0);
        return closed;
    }

    public async Task<bool> DeactivateAsync(
        string moduleId,
        CancellationToken cancellationToken)
    {
        try
        {
            var before = await _runtimeManager.GetSnapshotAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            if (before is null || !before.HasRuntimeRegistration || !before.IsActive)
            {
                return true;
            }

            await InvokeOnDispatcherAsync(
                () => _runtimeManager.DeactivateAsync(moduleId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var after = await _runtimeManager.GetSnapshotAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            var deactivated = after is not null && after.HasRuntimeRegistration && !after.IsActive;
            Log("Module deactivated for update", moduleId, after?.Version,
                after?.State.ToString(), deactivated ? "None" : "DeactivateFailed",
                after?.LoadContextGeneration ?? before.LoadContextGeneration);
            return deactivated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log("Module deactivated for update", moduleId, null, "Failed",
                exception.GetType().Name, 0);
            return false;
        }
    }

    public async Task<bool> UnloadAsync(
        string moduleId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await _windowManager.IsWindowOpenAsync(moduleId, cancellationToken).ConfigureAwait(false) &&
                !await RequestCloseWindowsAsync(moduleId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var before = await _runtimeManager.GetSnapshotAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            if (before is null || !before.HasRuntimeRegistration)
            {
                return true;
            }

            if (before.IsActive && !await DeactivateAsync(moduleId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            await CaptureDiagnosticAsync(before, null, cancellationToken).ConfigureAwait(false);
            await InvokeOnDispatcherAsync(
                () => _runtimeManager.UnloadAsync(moduleId, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var after = await _runtimeManager.GetSnapshotAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            var unloaded = after is not null && !after.HasRuntimeRegistration && !after.IsActive;
            if (_diagnostics.TryGetValue(moduleId, out var diagnostic))
            {
                _diagnostics[moduleId] = diagnostic with { IsLoaded = false };
            }

            Log("Module unload requested", moduleId, before.Version,
                after?.State.ToString(), unloaded ? "None" : "UnloadFailed",
                before.LoadContextGeneration);
            return unloaded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log("Module unload requested", moduleId, null, "Failed",
                exception.GetType().Name, 0);
            return false;
        }
    }

    public async Task<bool> VerifyUnloadedAsync(
        string moduleId,
        CancellationToken cancellationToken)
    {
        try
        {
            var before = await _runtimeManager.GetSnapshotAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            if (before?.HasRuntimeRegistration == true || before?.IsActive == true ||
                await _windowManager.IsWindowOpenAsync(moduleId, cancellationToken).ConfigureAwait(false))
            {
                Log("Module unload verified", moduleId, before?.Version,
                    before?.State.ToString(), "ModuleStillLoaded",
                    before?.LoadContextGeneration ?? 0);
                return false;
            }

            var weakReference = before?.LastUnloadedLoadContext;
            var collected = weakReference is null || await WaitForUnloadAsync(
                    weakReference, cancellationToken)
                .WaitAsync(UnloadVerificationTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!collected)
            {
                Log("Module unload verified", moduleId, before?.Version,
                    before?.State.ToString(), "LoadContextAlive",
                    before?.LoadContextGeneration ?? 0);
                return false;
            }

            var after = await _runtimeManager.GetSnapshotAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            var verified = after?.HasRuntimeRegistration != true && after?.IsActive != true &&
                           !await _windowManager.IsWindowOpenAsync(moduleId, cancellationToken)
                               .ConfigureAwait(false);
            if (_diagnostics.TryGetValue(moduleId, out var diagnostic))
            {
                _diagnostics[moduleId] = diagnostic with
                {
                    IsLoaded = false,
                    LoadContextCollected = verified
                };
            }

            Log("Module unload verified", moduleId, after?.Version ?? before?.Version,
                after?.State.ToString(), verified ? "None" : "ModuleStillLoaded",
                after?.LoadContextGeneration ?? before?.LoadContextGeneration ?? 0);
            return verified;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            Log("Module unload verified", moduleId, null, "Failed",
                exception.GetType().Name, 0);
            return false;
        }
    }

    public async Task<bool> RestorePreviousRuntimeStateAsync(
        string moduleId,
        ModuleUpdateRuntimeState previousState,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _deferRuntimeRestore) != 0)
        {
            _deferredRuntimeIntents[moduleId] = previousState;
            Log("Previous runtime intent deferred", moduleId, null,
                "RecoveryPending", "None", 0);
            return true;
        }

        try
        {
            var shouldBeLoaded = previousState.IsLoaded || previousState.IsActive || previousState.HasWindows;
            if (!shouldBeLoaded)
            {
                var current = await GetRuntimeStateAsync(moduleId, cancellationToken).ConfigureAwait(false);
                if (current.HasWindows &&
                    !await RequestCloseWindowsAsync(moduleId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
                if (current.IsActive && !await DeactivateAsync(moduleId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
                if (current.IsLoaded && !await UnloadAsync(moduleId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                var unloaded = await VerifyUnloadedAsync(moduleId, cancellationToken).ConfigureAwait(false);
                Log("Previous runtime intent restored", moduleId, null,
                    "Unloaded", unloaded ? "None" : "ModuleStillLoaded", 0);
                return unloaded;
            }

            var prepared = await DiscoverInstalledModuleAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            var currentRuntime = await _runtimeManager.GetSnapshotAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            if (currentRuntime?.HasRuntimeRegistration == true &&
                (!string.Equals(currentRuntime.Version, prepared.Module.Manifest.Version, StringComparison.Ordinal) ||
                 !Path.GetFullPath(currentRuntime.ModuleDirectory).Equals(
                     Path.GetFullPath(prepared.Module.ModuleDirectory), StringComparison.OrdinalIgnoreCase)))
            {
                Log("Previous runtime intent restored", moduleId, currentRuntime.Version,
                    currentRuntime.State.ToString(), "RuntimeVersionMismatch",
                    currentRuntime.LoadContextGeneration);
                return false;
            }

            if (currentRuntime?.HasRuntimeRegistration != true)
            {
                await _runtimeManager.RefreshDiscoveredModuleAsync(prepared.Module, cancellationToken)
                    .ConfigureAwait(false);
                await RegisterLocalizationAsync(prepared.Module, cancellationToken).ConfigureAwait(false);
                await InvokeOnDispatcherAsync(
                    () => _runtimeManager.LoadAsync(moduleId, _moduleDataRoot, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                currentRuntime = await _runtimeManager.GetSnapshotAsync(moduleId, cancellationToken)
                    .ConfigureAwait(false);
                if (currentRuntime is null || !currentRuntime.HasRuntimeRegistration)
                {
                    return false;
                }
                await CaptureDiagnosticAsync(currentRuntime, prepared.PayloadFingerprint, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (previousState.IsActive && currentRuntime?.IsActive != true)
            {
                await InvokeOnDispatcherAsync(
                    () => _runtimeManager.ActivateAsync(moduleId, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            else if (!previousState.IsActive && currentRuntime?.IsActive == true &&
                     !await DeactivateAsync(moduleId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var hasWindow = await _windowManager.IsWindowOpenAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            if (previousState.HasWindows && !hasWindow)
            {
                if (!await RestoreSingleWindowAsync(moduleId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }
            else if (!previousState.HasWindows && hasWindow &&
                     !await RequestCloseWindowsAsync(moduleId, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var finalRuntime = await _runtimeManager.GetSnapshotAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            var finalHasWindow = await _windowManager.IsWindowOpenAsync(moduleId, cancellationToken)
                .ConfigureAwait(false);
            var restored = finalRuntime?.HasRuntimeRegistration == true &&
                           finalRuntime.IsActive == previousState.IsActive &&
                           finalHasWindow == previousState.HasWindows &&
                           string.Equals(finalRuntime.Version, prepared.Module.Manifest.Version,
                               StringComparison.Ordinal);
            Log("Previous runtime intent restored", moduleId, finalRuntime?.Version,
                finalRuntime?.State.ToString(), restored ? "None" : "RuntimeIntentMismatch",
                finalRuntime?.LoadContextGeneration ?? 0);
            return restored;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log("Previous runtime intent restored", moduleId, null, "Failed",
                exception.GetType().Name, 0);
            return false;
        }
    }

    private async Task<(DiscoveredModule Module, string PayloadFingerprint)> DiscoverInstalledModuleAsync(
        string moduleId,
        CancellationToken cancellationToken)
    {
        var moduleDirectory = Path.GetFullPath(Path.Combine(_userModulesRoot, moduleId));
        if (!string.Equals(Path.GetDirectoryName(moduleDirectory), _userModulesRoot,
                StringComparison.OrdinalIgnoreCase) || !Directory.Exists(moduleDirectory))
        {
            throw new IOException("The installed module directory is unavailable.");
        }

        var manifestPath = Path.Combine(moduleDirectory, "module.json");
        var manifest = await _manifestReader.ReadAsync(manifestPath, cancellationToken)
            .ConfigureAwait(false);
        var errors = _manifestValidator.Validate(manifest, moduleDirectory, manifestPath);
        if (manifest is null || errors.Count != 0 ||
            !string.Equals(manifest.Id, moduleId, StringComparison.Ordinal))
        {
            throw new IOException("The installed module manifest is invalid.");
        }

        var payload = await ModuleStartupFingerprintService.ComputePayloadAsync(
                moduleDirectory, cancellationToken)
            .ConfigureAwait(false);
        if (payload.FileCount <= 0 || string.IsNullOrWhiteSpace(payload.Hash))
        {
            throw new IOException("The installed module payload fingerprint is unavailable.");
        }

        return (new DiscoveredModule
        {
            Manifest = manifest,
            ModuleDirectory = moduleDirectory,
            ManifestPath = manifestPath,
            State = ModuleState.NotLoaded,
            Errors = []
        }, payload.Hash);
    }

    private async Task<bool> RestoreSingleWindowAsync(
        string moduleId,
        CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return false;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            try
            {
                completion.TrySetResult(OpenSingleModuleWindow(
                    _runtimeManager, _windowManager, moduleId));
            }
            catch
            {
                completion.TrySetResult(false);
            }
        });

        try
        {
            return await completion.Task.WaitAsync(WindowOperationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool OpenSingleModuleWindow(
        ModuleRuntimeManager runtimeManager,
        ModuleWindowManager windowManager,
        string moduleId)
    {
        if (windowManager.IsWindowOpen(moduleId))
        {
            return true;
        }

        var record = runtimeManager.GetRecord(moduleId);
        var view = runtimeManager.CreateView(moduleId);
        if (record is null || view is null)
        {
            return false;
        }

        windowManager.OpenWindow(
            moduleId,
            record.Manifest.Name,
            view,
            Application.Current?.MainWindow);
        return windowManager.IsWindowOpen(moduleId);
    }

    private async Task CaptureDiagnosticAsync(
        ModuleRuntimeSnapshot runtime,
        string? knownFingerprint,
        CancellationToken cancellationToken)
    {
        if (!runtime.HasRuntimeRegistration)
        {
            return;
        }

        var fingerprint = knownFingerprint;
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            fingerprint = (await ModuleStartupFingerprintService.ComputePayloadAsync(
                    runtime.ModuleDirectory, cancellationToken)
                .ConfigureAwait(false)).Hash;
        }

        _diagnostics[runtime.ModuleId] = new(
            runtime.ModuleId,
            runtime.Version,
            HashIdentity(runtime.ModuleDirectory),
            fingerprint,
            runtime.LoadContextGeneration,
            runtime.RuntimeAssemblyInformationalVersion,
            true,
            false);
    }

    private static async Task<bool> WaitForUnloadAsync(
        WeakReference weakReference,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; weakReference.IsAlive && attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }, cancellationToken).ConfigureAwait(false);
            if (weakReference.IsAlive)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return !weakReference.IsAlive;
    }

    private void Log(
        string eventName,
        string moduleId,
        string? version,
        string? state,
        string failureCode,
        long generation) =>
        _log?.Invoke(new(
            eventName,
            moduleId,
            version ?? "unknown",
            state ?? "unknown",
            failureCode,
            generation));

    private async Task RegisterLocalizationAsync(
        DiscoveredModule module,
        CancellationToken cancellationToken)
    {
        if (_registerLocalization is null)
        {
            return;
        }

        await InvokeOnDispatcherAsync(
            () =>
            {
                _registerLocalization(module);
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task InvokeOnDispatcherAsync(
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            throw new InvalidOperationException("The WPF dispatcher is unavailable.");
        }

        if (dispatcher.CheckAccess())
        {
            await action().ConfigureAwait(true);
            return;
        }

        var operation = dispatcher.InvokeAsync(action, DispatcherPriority.Send, cancellationToken);
        await (await operation.Task.ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static string NormalizeRoot(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Runtime roots must be absolute.", parameterName);
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string HashIdentity(string path)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private sealed class RestoreDeferralScope(ModuleUpdateRuntimeCoordinator owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Volatile.Write(ref owner._deferRuntimeRestore, 0);
            }
        }
    }
}
