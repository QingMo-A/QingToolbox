using System.Net.NetworkInformation;
using Microsoft.Win32;
using QingToolbox.Modules.PowerGuard.Models;
using QingToolbox.Modules.PowerGuard.State;

namespace QingToolbox.Modules.PowerGuard.Services;

public interface IWarningPresenter
{
    Task ShowRealAsync(int seconds,CancellationToken token=default);
    Task<TestPreviewResult> ShowTestAsync(int seconds,CancellationToken token=default);
    Task UpdateRealAsync(int seconds,CancellationToken token=default);
    Task CloseRealAsync(CancellationToken token=default);
    Task CloseTestAsync(CancellationToken token=default);
    Task CloseAllAsync(CancellationToken token=default);
}

public sealed class PowerGuardController : IAsyncDisposable
{
    private readonly IConnectivityProbe _probe;
    private readonly IPowerActionService _power;
    private readonly PowerGuardSettingsStore _settingsStore;
    private readonly PowerGuardEventStore _events;
    private readonly IWarningPresenter _warning;
    private readonly PowerGuardStateMachine _machine = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly SemaphoreSlim _stateGate=new(1,1);
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private CancellationTokenSource? _countdownCts;private Task? _countdownTask;private int _pendingResume;
    private DateTimeOffset? _offlineSince;
    private DateTimeOffset? _recoverySince;
    private DateTimeOffset? _countdownDeadline;
    private DateTimeOffset? _lastOnline;
    private bool _subscribed;
    private bool _disposed;

    private PowerGuardSettings _settings;
    public PowerGuardSettings Settings=>PowerGuardSettingsStore.Clone(_settings);
    public PowerGuardSnapshot Snapshot { get; private set; }
    public event EventHandler<PowerGuardSnapshot>? SnapshotChanged;
    public event EventHandler? RecentEventsChanged { add => _events.EventAppended += value; remove => _events.EventAppended -= value; }
    public Task<IReadOnlyList<GuardEvent>> ReadRecentEventsAsync(CancellationToken token = default) => _events.ReadRecentAsync(12, token);

    public PowerGuardController(IConnectivityProbe probe, IPowerActionService power,
        PowerGuardSettingsStore settingsStore, PowerGuardEventStore events,
        IWarningPresenter warning, PowerGuardSettings settings)
    {
        _probe = probe; _power = power; _settingsStore = settingsStore; _events = events; _warning = warning;
        _settings = PowerGuardSettingsStore.Clone(settings);
        Snapshot = CreateSnapshot(false);
    }

    public async Task ActivateAsync(CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            if (_monitorTask is { IsCompleted:false }) return;
            token.ThrowIfCancellationRequested();if (_settings.GuardEnabled) Subscribe();
            _monitorCts = new();
            _monitorTask = MonitorSupervisorAsync(_monitorCts.Token);
            await _events.AppendAsync("Activated", token: token);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task DeactivateAsync(CancellationToken token = default)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            if (_monitorTask is null) return;
            _machine.MoveTo(PowerGuardState.Stopping, DateTimeOffset.UtcNow); Publish(false);
            _monitorCts!.Cancel();
            try { await _monitorTask; } catch (OperationCanceledException) { }
            _monitorTask = null; _monitorCts.Dispose(); _monitorCts = null;
            Unsubscribe();
            await StopCountdownTickerAsync();await _warning.CloseAllAsync(token);
            _countdownDeadline = null; _offlineSince = null; _recoverySince = null;
            _machine.MoveTo(PowerGuardState.Disabled, DateTimeOffset.UtcNow); Publish(false);
            await _events.AppendAsync("Deactivated", token: token);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task MonitorSupervisorAsync(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try{await MonitorAsync(token);}
            catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            catch(Exception exception)
            {
                await _stateGate.WaitAsync(token);try{_machine.MoveTo(PowerGuardState.MonitoringFault,DateTimeOffset.UtcNow);Publish(false,exception.GetType().Name);await _events.AppendAsync("MonitoringFault",exception.GetType().Name,token);}finally{_stateGate.Release();}
                await Task.Delay(TimeSpan.FromSeconds(10),token);
                await _stateGate.WaitAsync(token);try{_machine.MoveTo(PowerGuardState.StartupGrace,DateTimeOffset.UtcNow);Publish(false);await _events.AppendAsync("MonitoringRecovered",token:token);}finally{_stateGate.Release();}
            }
        }
    }
    private async Task MonitorAsync(CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        _machine.MoveTo(_settings.GuardEnabled ? PowerGuardState.StartupGrace : PowerGuardState.Disabled, now);
        Publish(false);
        while (!token.IsCancellationRequested)
        {
            if(Interlocked.Exchange(ref _pendingResume,0)!=0)await ApplyResumeAsync(token);
            if (!_settings.GuardEnabled)
            {
                await WaitAsync(TimeSpan.FromSeconds(10), token); continue;
            }
            var result = await ProbeSerializedAsync(token);
            now = DateTimeOffset.UtcNow;
            await ProcessProbeAsync(result, now, token);
            var delay = _machine.State switch
            {
                PowerGuardState.Online => 20,
                PowerGuardState.SuppressedForCurrentOutage => 10,
                _ => 5
            };
            await WaitAsync(TimeSpan.FromSeconds(delay), token);
        }
    }

    internal async Task ProcessProbeAsync(ConnectivityProbeResult result, DateTimeOffset now, CancellationToken token = default)
    {
        var runFinalProbe = false;
        await _stateGate.WaitAsync(token);try{
        if (result.IsOnline)
        {
            _lastOnline = now; _offlineSince = null;
            await _events.AppendAsync("ProbeSucceeded", token: token);
            if (_machine.State is PowerGuardState.Countdown or PowerGuardState.SuspectedOffline or PowerGuardState.ActionFailed)
            {
                _recoverySince = now; _countdownDeadline = null;
                _machine.MoveTo(PowerGuardState.Recovering, now); await StopCountdownTickerAsync();await _warning.CloseRealAsync(token);
            }
            if (_machine.State == PowerGuardState.SuppressedForCurrentOutage) _recoverySince ??= now;
            if (_machine.State == PowerGuardState.StartupGrace && Elapsed(now) >= Settings.StartupGraceSeconds ||
                (_machine.State is PowerGuardState.Recovering or PowerGuardState.SuppressedForCurrentOutage) &&
                _recoverySince is { } recovery && SecondsBetween(recovery, now) >= Settings.RecoveryConfirmationSeconds)
            {
                _settings=await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=false,token);
                _recoverySince = null; _machine.MoveTo(PowerGuardState.Online, now);
                await _events.AppendAsync("ConnectivityRecovered", token: token);
            }
            Publish(true); return;
        }

        await _events.AppendAsync("ProbeFailed", result.Endpoints.FirstOrDefault()?.FailureCategory, token);
        _recoverySince = null;
        if (_settings.SuppressedUntilConnectivityRestored || _machine.State == PowerGuardState.SuppressedForCurrentOutage)
        {
            _machine.MoveTo(PowerGuardState.SuppressedForCurrentOutage, now); Publish(false); return;
        }
        if (_machine.State == PowerGuardState.StartupGrace && Elapsed(now) < _settings.StartupGraceSeconds) { Publish(false); return; }
        if (_machine.State is PowerGuardState.Online or PowerGuardState.StartupGrace or PowerGuardState.Recovering)
        {
            _offlineSince = now; _machine.MoveTo(PowerGuardState.SuspectedOffline, now);
            await _events.AppendAsync("OfflineSuspected", token: token);
        }
        if (_machine.State == PowerGuardState.SuspectedOffline && _offlineSince is { } offline &&
            SecondsBetween(offline, now) >= _settings.OfflineConfirmationSeconds)
        {
            _countdownDeadline = now.AddSeconds(_settings.ShutdownCountdownSeconds);
            _machine.MoveTo(PowerGuardState.Countdown, now);
            await _warning.ShowRealAsync(_settings.ShutdownCountdownSeconds, token);StartCountdownTicker();
            await _events.AppendAsync("CountdownStarted", token: token);
        }
        if (_machine.State == PowerGuardState.Countdown && _countdownDeadline is { } deadline)
        {
            var remaining = Math.Max(0, (int)Math.Ceiling((deadline - now).TotalSeconds));
            await _warning.UpdateRealAsync(remaining, token);
            if (remaining == 0) runFinalProbe = true;
        }
        Publish(false);
        }finally{_stateGate.Release();}
        if (runFinalProbe) await ExecuteAfterFinalProbeAsync(token);
    }

    private async Task ExecuteAfterFinalProbeAsync(CancellationToken token)
    {
        var final = await ProbeSerializedAsync(token);
        await _stateGate.WaitAsync(token);
        try
        {
        if (_machine.State != PowerGuardState.Countdown || _countdownDeadline is null) return;
        if (final.IsOnline)
        {
            _machine.MoveTo(PowerGuardState.Recovering, DateTimeOffset.UtcNow); _recoverySince = DateTimeOffset.UtcNow;
            _countdownDeadline = null;await StopCountdownTickerAsync(); await _warning.CloseRealAsync(token); Publish(true); return;
        }
        _machine.MoveTo(PowerGuardState.ExecutingShutdown, DateTimeOffset.UtcNow); Publish(false);
        _countdownDeadline = null;
        await StopCountdownTickerAsync();
        await _warning.CloseRealAsync(token);
        await _events.AppendAsync("ShutdownRequested", token: token);
        if (await _power.RequestNormalShutdownAsync(token)==PowerActionResult.Failed)
        {
            _machine.MoveTo(PowerGuardState.ActionFailed, DateTimeOffset.UtcNow);
            await _events.AppendAsync("ShutdownFailed", token: token); Publish(false, "ShutdownFailed");
        }
        }
        finally { _stateGate.Release(); }
    }

    public async Task SuppressCurrentOutageAsync(CancellationToken token = default)
    {
        await _stateGate.WaitAsync(token);try{
        _settings=await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=true,token);
        _countdownDeadline = null; _machine.MoveTo(PowerGuardState.SuppressedForCurrentOutage, DateTimeOffset.UtcNow);
        await StopCountdownTickerAsync();await _warning.CloseRealAsync(token); await _events.AppendAsync("OutageSuppressed", token: token); Publish(false);
        }finally{_stateGate.Release();}
    }

    public async Task RearmAsync(CancellationToken token = default)
    {
        await _stateGate.WaitAsync(token);try{_settings=await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=false,token);
        _offlineSince = DateTimeOffset.UtcNow; _machine.MoveTo(PowerGuardState.SuspectedOffline, DateTimeOffset.UtcNow); Publish(false);
        }finally{_stateGate.Release();}
    }
    public async Task ExtendCountdownAsync(CancellationToken token = default)
    {
        await _stateGate.WaitAsync(token);try{if (_countdownDeadline is null) return;
        _countdownDeadline = _countdownDeadline.Value.AddMinutes(10);
        await _events.AppendAsync("CountdownExtended", token: token); Publish(false);
        }finally{_stateGate.Release();}
    }
    public Task<ConnectivityProbeResult> ProbeNowAsync(CancellationToken token = default) => ProbeSerializedAsync(token);
    public Task<TestPreviewResult> ShowTestWarningAsync(CancellationToken token = default) => _warning.ShowTestAsync(30, token);
    public async Task ShutdownNowAsync(CancellationToken token = default)
    {
        await _stateGate.WaitAsync(token);try{_machine.MoveTo(PowerGuardState.ExecutingShutdown, DateTimeOffset.UtcNow); Publish(false);
        await _events.AppendAsync("ShutdownRequested", "Manual", token);
        if (await _power.RequestNormalShutdownAsync(token)==PowerActionResult.Failed)
        {
            _machine.MoveTo(PowerGuardState.ActionFailed, DateTimeOffset.UtcNow);
            await _events.AppendAsync("ShutdownFailed", token: token); Publish(false, "ShutdownFailed");
        }}finally{_stateGate.Release();}
    }

    public async Task SaveSettingsAsync(PowerGuardSettings settings, CancellationToken token = default)
    {
        settings.Normalize();await _stateGate.WaitAsync(token);try{_settings=await _settingsStore.UpdateAsync(s=>{s.GuardEnabled=settings.GuardEnabled;s.StartupGraceSeconds=settings.StartupGraceSeconds;s.OfflineConfirmationSeconds=settings.OfflineConfirmationSeconds;s.ShutdownCountdownSeconds=settings.ShutdownCountdownSeconds;s.RecoveryConfirmationSeconds=settings.RecoveryConfirmationSeconds;s.ShowRecoveryNotification=settings.ShowRecoveryNotification;},token);
        if (!settings.GuardEnabled)
        {
            Unsubscribe();
            _countdownDeadline = null; _offlineSince = null; _recoverySince = null;
            _machine.MoveTo(PowerGuardState.Disabled, DateTimeOffset.UtcNow);await StopCountdownTickerAsync();await _warning.CloseRealAsync(token);
        }
        else if (_machine.State == PowerGuardState.Disabled)
        {
            Subscribe();
            _machine.MoveTo(PowerGuardState.StartupGrace, DateTimeOffset.UtcNow);
        }
        await _events.AppendAsync("SettingsChanged", token: token); Signal(); Publish(Snapshot.IsOnline);
        }finally{_stateGate.Release();}
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _subscribed = true;
    }
    private void Unsubscribe()
    {
        if (!_subscribed) return;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _subscribed = false;
    }
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Signal();
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        Interlocked.Exchange(ref _pendingResume,1);Signal();
    }
    private async Task ApplyResumeAsync(CancellationToken token){await _stateGate.WaitAsync(token);try{_offlineSince=null;_recoverySince=null;_countdownDeadline=null;_machine.MoveTo(PowerGuardState.StartupGrace,DateTimeOffset.UtcNow);await StopCountdownTickerAsync();await _warning.CloseRealAsync(token);await _events.AppendAsync("ResumeGraceStarted",token:token);Publish(false);}finally{_stateGate.Release();}}
    private void Signal() { try { if (_signal.CurrentCount == 0) _signal.Release(); } catch (SemaphoreFullException) { } }
    private async Task<ConnectivityProbeResult> ProbeSerializedAsync(CancellationToken token)
    {
        await _probeGate.WaitAsync(token);
        try { return await _probe.ProbeAsync(token); }
        finally { _probeGate.Release(); }
    }
    private async Task WaitAsync(TimeSpan delay, CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timer = Task.Delay(delay, wait.Token); var signal = _signal.WaitAsync(wait.Token);
        await Task.WhenAny(timer, signal);
        wait.Cancel();
        try { await Task.WhenAll(timer, signal); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        token.ThrowIfCancellationRequested();
    }
    private static void Observe(Task task, string context) => _ = task.ContinueWith(
        completed => System.Diagnostics.Debug.WriteLine($"PowerGuard {context} failed: {completed.Exception?.GetBaseException().GetType().Name}"),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private int Elapsed(DateTimeOffset now) => SecondsBetween(_machine.StateSinceUtc, now);
    private static int SecondsBetween(DateTimeOffset from, DateTimeOffset to) => Math.Max(0, (int)(to - from).TotalSeconds);
    private PowerGuardSnapshot CreateSnapshot(bool online, string? error = null) => new(_machine.State, _settings.GuardEnabled, online, _lastOnline,
        _countdownDeadline is null ? 0 : Math.Max(0, (int)Math.Ceiling((_countdownDeadline.Value - DateTimeOffset.UtcNow).TotalSeconds)), _settings.SuppressedUntilConnectivityRestored, error);
    private void Publish(bool online, string? error = null) { Snapshot = CreateSnapshot(online, error); try { SnapshotChanged?.Invoke(this, Snapshot); } catch { } }
    internal void StartForTesting(DateTimeOffset now) { _machine.MoveTo(PowerGuardState.StartupGrace, now); Publish(false); }
    internal void ResumeForTesting(DateTimeOffset now) { _offlineSince=null;_recoverySince=null;_countdownDeadline=null;_machine.MoveTo(PowerGuardState.StartupGrace,now);Publish(false); }

    private void StartCountdownTicker(){if(_countdownTask is {IsCompleted:false})return;_countdownCts=new();_countdownTask=CountdownTickerAsync(_countdownCts.Token);}
    private async Task CountdownTickerAsync(CancellationToken token){try{while(!token.IsCancellationRequested){var seconds=_countdownDeadline is null?0:Math.Max(0,(int)Math.Ceiling((_countdownDeadline.Value-DateTimeOffset.UtcNow).TotalSeconds));await _warning.UpdateRealAsync(seconds,token);Publish(false);await Task.Delay(TimeSpan.FromSeconds(1),token);}}catch(OperationCanceledException)when(token.IsCancellationRequested){}}
    private async Task StopCountdownTickerAsync(){var cts=_countdownCts;var task=_countdownTask;_countdownCts=null;_countdownTask=null;if(cts is null)return;cts.Cancel();if(task is not null)try{await task;}catch(OperationCanceledException){}cts.Dispose();}

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        await DeactivateAsync(); Unsubscribe(); _lifecycle.Dispose(); _probeGate.Dispose(); _signal.Dispose();_stateGate.Dispose();
    }
}
