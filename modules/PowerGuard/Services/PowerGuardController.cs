using System.Net.NetworkInformation;
using Microsoft.Win32;
using QingToolbox.Modules.PowerGuard.Models;
using QingToolbox.Modules.PowerGuard.State;

namespace QingToolbox.Modules.PowerGuard.Services;
public sealed class PowerGuardCleanupException(string message):Exception(message);

public interface IWarningPresenter
{
    Task ShowRealAsync(int seconds,CancellationToken token=default); Task<TestPreviewResult> ShowTestAsync(int seconds,CancellationToken token=default);
    Task UpdateRealAsync(int seconds,CancellationToken token=default); Task CloseRealAsync(CancellationToken token=default);
    Task CloseTestAsync(CancellationToken token=default); Task CloseAllAsync(CancellationToken token=default);
}

public sealed class PowerGuardController:IAsyncDisposable
{
    private readonly IConnectivityProbe _probe; private readonly IPowerActionService _power; private readonly PowerGuardSettingsStore _settingsStore;
    private readonly PowerGuardEventStore _events; private readonly IWarningPresenter _warning; private readonly PowerGuardStateMachine _machine=new();
    private readonly SemaphoreSlim _lifecycle=new(1,1),_probeGate=new(1,1),_signal=new(0,1),_stateGate=new(1,1),_settingsGate=new(1,1),_presentationGate=new(1,1);
    private CancellationTokenSource? _monitorCts,_countdownCts,_lifetimeCts; private Task? _monitorTask,_countdownTask;
    private DateTimeOffset? _offlineSince,_recoverySince,_countdownDeadline,_lastOnline; private Guid? _countdownSession,_finalProbeSession;
    private PowerGuardSettings _settings; private bool _subscribed,_disposed,_active; private int _pendingResume,_monitorGeneration; private long _epoch;private Guid? _settingsReservation,_tickerSession;private readonly object _disposeSync=new();private Task? _disposeTask;
    private readonly TimeSpan _faultBackoff,_cleanupTimeout;
    public PowerGuardSettings Settings=>PowerGuardSettingsStore.Clone(Volatile.Read(ref _settings)); public PowerGuardSnapshot Snapshot{get;private set;}
    public event EventHandler<PowerGuardSnapshot>? SnapshotChanged; public event EventHandler? RecentEventsChanged{add=>_events.EventAppended+=value;remove=>_events.EventAppended-=value;}
    public Task<IReadOnlyList<GuardEvent>> ReadRecentEventsAsync(CancellationToken token=default)=>_events.ReadRecentAsync(20,token);

    public PowerGuardController(IConnectivityProbe probe,IPowerActionService power,PowerGuardSettingsStore settingsStore,PowerGuardEventStore events,IWarningPresenter warning,PowerGuardSettings settings,TimeSpan? faultBackoff=null,TimeSpan? cleanupTimeout=null)
    { _probe=probe;_power=power;_settingsStore=settingsStore;_events=events;_warning=warning;_settings=PowerGuardSettingsStore.Clone(settings);_faultBackoff=faultBackoff??TimeSpan.FromSeconds(10);_cleanupTimeout=cleanupTimeout??TimeSpan.FromSeconds(15);Snapshot=CreateSnapshot(false); }

    public async Task ActivateAsync(CancellationToken token=default)
    {
        await _lifecycle.WaitAsync(token); try
        {
            if(_disposed)throw new ObjectDisposedException(nameof(PowerGuardController)); if(_monitorTask is {IsCompleted:false})return;
            if(_monitorTask is not null){try{await _monitorTask;}catch(Exception e){System.Diagnostics.Debug.WriteLine(e.GetType().Name);}_monitorCts?.Dispose();}
            token.ThrowIfCancellationRequested(); _active=true; var generation=++_monitorGeneration; _monitorCts=new();_lifetimeCts?.Dispose();_lifetimeCts=new();
            await _stateGate.WaitAsync(token);try{if(!TryTransition(_settings.GuardEnabled?PowerGuardState.StartupGrace:PowerGuardState.Disabled,DateTimeOffset.UtcNow))throw new InvalidOperationException("PowerGuard activation invariant failed.");Publish(false);}finally{_stateGate.Release();}
            if(_settings.GuardEnabled)Subscribe(); _monitorTask=MonitorSupervisorAsync(generation,_monitorCts.Token); await _events.AppendAsync("Activated",token:token);
        } finally{_lifecycle.Release();}
    }

    public async Task DeactivateAsync(CancellationToken token=default)
    {
        await _lifecycle.WaitAsync(token); try
        {
            _active=false;++_monitorGeneration; CancellationTokenSource? cts,lifetime;Task? task;
            await _stateGate.WaitAsync(token);try{if(_machine.State!=PowerGuardState.Disabled&&!TryTransition(PowerGuardState.Stopping,DateTimeOffset.UtcNow))throw new PowerGuardCleanupException("PowerGuard could not enter the stopping state.");InvalidateCountdown();Publish(false);cts=_monitorCts;lifetime=_lifetimeCts;task=_monitorTask;}finally{_stateGate.Release();}
            cts?.Cancel();lifetime?.Cancel();Unsubscribe();
            if(task is not null)try{await task.WaitAsync(_cleanupTimeout,CancellationToken.None);}catch(TimeoutException){System.Diagnostics.Debug.WriteLine("PowerGuard monitor cleanup timeout");throw new PowerGuardCleanupException("PowerGuard background monitoring did not stop safely.");}catch(OperationCanceledException){}catch(Exception e){System.Diagnostics.Debug.WriteLine(e.GetType().Name);}
            try{await StopCountdownPresentationAsync(CancellationToken.None).WaitAsync(_cleanupTimeout);}catch(TimeoutException){System.Diagnostics.Debug.WriteLine("PowerGuard presentation cleanup timeout");throw new PowerGuardCleanupException("PowerGuard presentation did not stop safely.");}await SafeCloseAllAsync();
            await _stateGate.WaitAsync(CancellationToken.None);try{_offlineSince=_recoverySince=_countdownDeadline=null;if(!TryTransition(PowerGuardState.Disabled,DateTimeOffset.UtcNow))throw new PowerGuardCleanupException("PowerGuard could not finish deactivation.");Publish(false);_monitorTask=null;_monitorCts=null;_lifetimeCts=null;}finally{_stateGate.Release();}
            cts?.Dispose();lifetime?.Dispose();await _events.AppendAsync("Deactivated",token:CancellationToken.None);
        } finally{_lifecycle.Release();}
    }

    private async Task MonitorSupervisorAsync(int generation,CancellationToken token)
    {
        while(!token.IsCancellationRequested&&IsCurrentMonitor(generation))
        {
            try{await MonitorAsync(generation,token);}
            catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
            catch(Exception exception)
            {
                if(!await EnterMonitoringFaultAsync(generation,exception,token))break;
                try{await Task.Delay(_faultBackoff,token);}catch(OperationCanceledException)when(token.IsCancellationRequested){break;}
                if(!await RecoverMonitoringAsync(generation,token))break;
            }
        }
    }
    private async Task<bool> EnterMonitoringFaultAsync(int generation,Exception exception,CancellationToken token)
    {
        await _stateGate.WaitAsync(token);try{if(!IsCurrentMonitor(generation)||_machine.State==PowerGuardState.Stopping||!TryTransition(PowerGuardState.MonitoringFault,DateTimeOffset.UtcNow))return false;_offlineSince=_recoverySince=null;InvalidateCountdown();Publish(false,"MonitoringFault");}finally{_stateGate.Release();}
        await StopCountdownPresentationAsync(token);await _events.AppendAsync("MonitoringFault",token:token);System.Diagnostics.Debug.WriteLine(exception.GetType().Name);return true;
    }
    private async Task<bool> RecoverMonitoringAsync(int generation,CancellationToken token)
    {
        await _stateGate.WaitAsync(token);try{if(!IsCurrentMonitor(generation)||_machine.State!=PowerGuardState.MonitoringFault||!TryTransition(_settings.GuardEnabled?PowerGuardState.StartupGrace:PowerGuardState.Disabled,DateTimeOffset.UtcNow))return false;Publish(false);}finally{_stateGate.Release();}
        await _events.AppendAsync("MonitoringRecovered",token:token);return true;
    }
    private bool IsCurrentMonitor(int generation)=>_active&&generation==Volatile.Read(ref _monitorGeneration);
    private async Task MonitorAsync(int generation,CancellationToken token)
    {
        while(!token.IsCancellationRequested&&IsCurrentMonitor(generation))
        {
            if(Interlocked.Exchange(ref _pendingResume,0)!=0)await ApplyResumeAsync(token);
            if(!_settings.GuardEnabled){await WaitAsync(TimeSpan.FromSeconds(10),token);continue;}
            var result=await ProbeSerializedAsync(token);await ProcessProbeAsync(result,DateTimeOffset.UtcNow,token);
            var delay=Snapshot.State switch{PowerGuardState.Online=>20,PowerGuardState.SuppressedForCurrentOutage=>10,_=>5};await WaitAsync(TimeSpan.FromSeconds(delay),token);
        }
    }

    internal async Task ProcessProbeAsync(ConnectivityProbeResult result,DateTimeOffset now,CancellationToken token=default)
    {
        bool close=false,show=false,clearSuppression=false;Guid? finalSession=null,showSession=null;long showEpoch=0;int showSeconds=0;string eventName=result.IsOnline?"ProbeSucceeded":"ProbeFailed";
        await _stateGate.WaitAsync(token);try
        {
            if(_machine.State is PowerGuardState.Stopping or PowerGuardState.Disabled or PowerGuardState.MonitoringFault)return;
            if(result.IsOnline)
            {
                _lastOnline=now;_offlineSince=null;
                if(_machine.State is PowerGuardState.Countdown or PowerGuardState.SuspectedOffline or PowerGuardState.ActionFailed){if(TryTransition(PowerGuardState.Recovering,now)){_recoverySince=now;InvalidateCountdown();close=true;}else return;}
                if(_machine.State==PowerGuardState.SuppressedForCurrentOutage)_recoverySince??=now;
                if((_machine.State==PowerGuardState.StartupGrace&&Elapsed(now)>=_settings.StartupGraceSeconds)||((_machine.State is PowerGuardState.Recovering or PowerGuardState.SuppressedForCurrentOutage)&&_recoverySince is{} recovery&&SecondsBetween(recovery,now)>=_settings.RecoveryConfirmationSeconds)){if(!TryTransition(PowerGuardState.Online,now))return;_recoverySince=null;clearSuppression=true;eventName="ConnectivityRecovered";}
            }
            else
            {
                _recoverySince=null;
                if(_settings.SuppressedUntilConnectivityRestored||_machine.State==PowerGuardState.SuppressedForCurrentOutage){if(!TryTransition(PowerGuardState.SuppressedForCurrentOutage,now))return;}
                else if(!(_machine.State==PowerGuardState.StartupGrace&&Elapsed(now)<_settings.StartupGraceSeconds))
                {
                    if(_machine.State is PowerGuardState.Online or PowerGuardState.StartupGrace or PowerGuardState.Recovering){if(!TryTransition(PowerGuardState.SuspectedOffline,now))return;_offlineSince=now;eventName="OfflineSuspected";}
                    if(_machine.State==PowerGuardState.SuspectedOffline&&_offlineSince is{} offline&&SecondsBetween(offline,now)>=_settings.OfflineConfirmationSeconds){if(!TryTransition(PowerGuardState.Countdown,now))return;_countdownDeadline=now.AddSeconds(_settings.ShutdownCountdownSeconds);_countdownSession=Guid.NewGuid();showSession=_countdownSession;showEpoch=++_epoch;showSeconds=_settings.ShutdownCountdownSeconds;show=true;eventName="CountdownStarted";}
                    if(_machine.State==PowerGuardState.Countdown&&_countdownDeadline<=now)finalSession=_countdownSession;
                }
            }
            Publish(result.IsOnline);
        }finally{_stateGate.Release();}
        if(clearSuppression)await ClearSuppressionAfterRecoveryAsync(result.IsOnline,token);
        if(close)await StopCountdownPresentationAsync(token);if(show&&showSession is{} presentationSession)await StartCountdownPresentationAsync(presentationSession,showEpoch,showSeconds,token);
        await _events.AppendAsync(eventName,result.IsOnline?null:SafeFailure(result),token); if(finalSession is{} id)await ExecuteAfterFinalProbeAsync(id,token);
    }

    private async Task ExecuteAfterFinalProbeAsync(Guid session,CancellationToken token)
    {
        long finalEpoch;await _stateGate.WaitAsync(token);try{if(!_active||!_settings.GuardEnabled||_machine.State!=PowerGuardState.Countdown||_countdownSession!=session||_finalProbeSession is not null||_settingsReservation is not null)return;_finalProbeSession=session;finalEpoch=_epoch;}finally{_stateGate.Release();}
        var final=await ProbeSerializedAsync(token);bool execute=false,recover=false;
        await _stateGate.WaitAsync(token);try
        {
            if(_finalProbeSession==session)_finalProbeSession=null;
            if(_machine.State!=PowerGuardState.Countdown||_countdownSession!=session||_epoch!=finalEpoch||!_settings.GuardEnabled||!_active||_settingsReservation is not null)return;
            if(final.IsOnline){if(!TryTransition(PowerGuardState.Recovering,DateTimeOffset.UtcNow))return;_recoverySince=DateTimeOffset.UtcNow;_countdownDeadline=null;recover=true;}else{if(!TryTransition(PowerGuardState.ExecutingShutdown,DateTimeOffset.UtcNow))return;_countdownDeadline=null;execute=true;}Publish(final.IsOnline);
        }finally{_stateGate.Release();}
        await StopCountdownPresentationAsync(token);
        if(recover)return;if(!execute)return;await _events.AppendAsync("ShutdownRequested",token:token);var result=await _power.RequestNormalShutdownAsync(token);
        if(result==PowerActionResult.Failed){await _stateGate.WaitAsync(token);try{if(_machine.State==PowerGuardState.ExecutingShutdown&&TryTransition(PowerGuardState.ActionFailed,DateTimeOffset.UtcNow))Publish(false,"ShutdownFailed");}finally{_stateGate.Release();}await _events.AppendAsync("ShutdownFailed",token:token);}
    }
    public async Task<GuardOperationResult> SuppressCurrentOutageAsync(CancellationToken token=default)
    {
        if(!IsAcceptingOperations)return GuardOperationResult.NotAvailable;
        using var linked=CreateOperationToken(token);await _settingsGate.WaitAsync(linked.Token);try
        {
            Guid reservation,session;long epoch;int seconds;
            await _stateGate.WaitAsync(linked.Token);try{if(!Snapshot.CanSuppressCurrentOutage||_countdownSession is not{} current||_settingsReservation is not null)return GuardOperationResult.NotAvailable;reservation=Guid.NewGuid();_settingsReservation=reservation;session=current;epoch=_epoch;seconds=Snapshot.RemainingSeconds;}finally{_stateGate.Release();}
            PowerGuardSettings saved;try{saved=await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=true,linked.Token);}catch(Exception e)when(e is not OperationCanceledException){await ClearReservationAsync(reservation);await StartCountdownPresentationAsync(session,epoch,seconds,CancellationToken.None);return GuardOperationResult.Failed;}
            var applied=false;await _stateGate.WaitAsync(linked.Token);try{if(_settingsReservation==reservation&&_machine.State==PowerGuardState.Countdown&&_countdownSession==session&&_epoch==epoch&&TryTransition(PowerGuardState.SuppressedForCurrentOutage,DateTimeOffset.UtcNow)){_settings=WithSuppression(_settings,true);InvalidateCountdown();Publish(false);applied=true;}_settingsReservation=null;}finally{_stateGate.Release();}
            if(!applied){var actual=Settings.SuppressedUntilConnectivityRestored;await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=actual,linked.Token);return GuardOperationResult.AppliedButStateChanged;}
            await StopCountdownPresentationAsync(linked.Token);await _events.AppendAsync("OutageSuppressed",token:linked.Token);return GuardOperationResult.Succeeded;
        }finally{_settingsGate.Release();}
    }
    public async Task<GuardOperationResult> RearmAsync(CancellationToken token=default)
    {
        if(!IsAcceptingOperations)return GuardOperationResult.NotAvailable;
        using var linked=CreateOperationToken(token);await _settingsGate.WaitAsync(linked.Token);try
        {
            Guid reservation;long epoch;await _stateGate.WaitAsync(linked.Token);try{if(!Snapshot.CanRearmCurrentOutage||_settingsReservation is not null)return GuardOperationResult.NotAvailable;reservation=Guid.NewGuid();_settingsReservation=reservation;epoch=_epoch;}finally{_stateGate.Release();}
            PowerGuardSettings saved;try{saved=await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=false,linked.Token);}catch(Exception e)when(e is not OperationCanceledException){await ClearReservationAsync(reservation);return GuardOperationResult.Failed;}
            var applied=false;await _stateGate.WaitAsync(linked.Token);try{if(_settingsReservation==reservation&&_machine.State==PowerGuardState.SuppressedForCurrentOutage&&_epoch==epoch&&TryTransition(PowerGuardState.SuspectedOffline,DateTimeOffset.UtcNow)){_settings=WithSuppression(_settings,false);_offlineSince=DateTimeOffset.UtcNow;_epoch++;Publish(false);applied=true;}_settingsReservation=null;}finally{_stateGate.Release();}
            if(!applied){await _stateGate.WaitAsync(linked.Token);try{_settings=WithSuppression(_settings,saved.SuppressedUntilConnectivityRestored);Publish(false);}finally{_stateGate.Release();}return GuardOperationResult.AppliedButStateChanged;}return GuardOperationResult.Succeeded;
        }finally{_settingsGate.Release();}
    }
    public async Task<GuardOperationResult> ExtendCountdownAsync(CancellationToken token=default)
    {
        if(!IsAcceptingOperations)return GuardOperationResult.NotAvailable;
        Guid session;long epoch;int seconds;await _stateGate.WaitAsync(token);try{if(!Snapshot.CanExtendCountdown||_countdownDeadline is null||_countdownSession is null)return GuardOperationResult.NotAvailable;_countdownDeadline=_countdownDeadline.Value.AddMinutes(10);session=Guid.NewGuid();_countdownSession=session;epoch=++_epoch;Publish(false);seconds=Snapshot.RemainingSeconds;}finally{_stateGate.Release();}await StartCountdownPresentationAsync(session,epoch,seconds,token);await _events.AppendAsync("CountdownExtended",token:token);return GuardOperationResult.Succeeded;
    }
    public async Task<(GuardOperationResult Result,ConnectivityProbeResult? Probe)> ProbeNowAsync(CancellationToken token=default){if(!IsAcceptingOperations||!Snapshot.CanProbeNow)return(GuardOperationResult.NotAvailable,null);return(GuardOperationResult.Succeeded,await ProbeSerializedAsync(token));}
    public async Task<TestPreviewResult> ShowTestWarningAsync(CancellationToken token=default)=>IsAcceptingOperations&&Snapshot.CanTestWarning?await _warning.ShowTestAsync(30,token):TestPreviewResult.UnavailableDuringCountdown;
    public async Task<GuardOperationResult> ShutdownNowAsync(CancellationToken token=default)
    {
        if(!IsAcceptingOperations)return GuardOperationResult.NotAvailable;
        await _stateGate.WaitAsync(token);try{if(!Snapshot.CanRequestShutdownNow||!TryTransition(PowerGuardState.ExecutingShutdown,DateTimeOffset.UtcNow))return GuardOperationResult.NotAvailable;InvalidateCountdown();Publish(false);}finally{_stateGate.Release();}
        await StopCountdownPresentationAsync(token);await _events.AppendAsync("ShutdownRequested","Manual",token);var r=await _power.RequestNormalShutdownAsync(token);if(r!=PowerActionResult.Failed)return GuardOperationResult.Succeeded;
        await _stateGate.WaitAsync(token);try{if(TryTransition(PowerGuardState.ActionFailed,DateTimeOffset.UtcNow))Publish(false,"ShutdownFailed");}finally{_stateGate.Release();}await _events.AppendAsync("ShutdownFailed",token:token);return GuardOperationResult.Failed;
    }
    public async Task<GuardOperationResult> SaveSettingsAsync(PowerGuardSettings settings,CancellationToken token=default)
    {
        if(!IsAcceptingOperations)return GuardOperationResult.NotAvailable;if(!SettingsAreValid(settings))return GuardOperationResult.Failed;using var linked=CreateOperationToken(token);await _settingsGate.WaitAsync(linked.Token);try
        {
            Guid reservation;long epoch;await _stateGate.WaitAsync(linked.Token);try{if(!Snapshot.CanChangeSettings||_settingsReservation is not null)return GuardOperationResult.NotAvailable;reservation=Guid.NewGuid();_settingsReservation=reservation;epoch=_epoch;}finally{_stateGate.Release();}
            PowerGuardSettings saved;try{saved=await _settingsStore.UpdateAsync(s=>{s.GuardEnabled=settings.GuardEnabled;s.StartupGraceSeconds=settings.StartupGraceSeconds;s.OfflineConfirmationSeconds=settings.OfflineConfirmationSeconds;s.ShutdownCountdownSeconds=settings.ShutdownCountdownSeconds;s.RecoveryConfirmationSeconds=settings.RecoveryConfirmationSeconds;s.ShowRecoveryNotification=settings.ShowRecoveryNotification;},linked.Token);}catch(Exception e)when(e is not OperationCanceledException){await ClearReservationAsync(reservation);return GuardOperationResult.Failed;}
            bool close=false,stateChanged;await _stateGate.WaitAsync(linked.Token);try{stateChanged=_epoch!=epoch;_settings=saved;_settingsReservation=null;if(!saved.GuardEnabled&&_machine.State is not(PowerGuardState.ExecutingShutdown or PowerGuardState.Stopping)){if(TryTransition(PowerGuardState.Disabled,DateTimeOffset.UtcNow)){InvalidateCountdown();_offlineSince=_recoverySince=null;close=true;}}else if(saved.GuardEnabled&&_machine.State==PowerGuardState.Disabled)TryTransition(PowerGuardState.StartupGrace,DateTimeOffset.UtcNow);Publish(false);}finally{_stateGate.Release();}
            if(close){Unsubscribe();await StopCountdownPresentationAsync(linked.Token);}else if(saved.GuardEnabled)Subscribe();await _events.AppendAsync("SettingsChanged",token:linked.Token);Signal();return stateChanged?GuardOperationResult.AppliedButStateChanged:GuardOperationResult.Succeeded;
        }finally{_settingsGate.Release();}
    }

    private async Task ApplyResumeAsync(CancellationToken token){await _stateGate.WaitAsync(token);try{if(!_active||_machine.State==PowerGuardState.Stopping)return;var target=_settings.GuardEnabled?PowerGuardState.StartupGrace:PowerGuardState.Disabled;if(!TryTransition(target,DateTimeOffset.UtcNow))return;InvalidateCountdown();_offlineSince=_recoverySince=null;Publish(false);}finally{_stateGate.Release();}await StopCountdownPresentationAsync(token);await _events.AppendAsync("ResumeGraceStarted",token:token);}
    private void Subscribe(){if(_subscribed)return;NetworkChange.NetworkAvailabilityChanged+=OnNetworkAvailabilityChanged;SystemEvents.PowerModeChanged+=OnPowerModeChanged;_subscribed=true;}
    private void Unsubscribe(){if(!_subscribed)return;NetworkChange.NetworkAvailabilityChanged-=OnNetworkAvailabilityChanged;SystemEvents.PowerModeChanged-=OnPowerModeChanged;_subscribed=false;}
    private void OnNetworkAvailabilityChanged(object? s,NetworkAvailabilityEventArgs e)=>Signal(); private void OnPowerModeChanged(object s,PowerModeChangedEventArgs e){if(e.Mode==PowerModes.Resume){Interlocked.Exchange(ref _pendingResume,1);Signal();}}
    private async Task<ConnectivityProbeResult> ProbeSerializedAsync(CancellationToken token){await _probeGate.WaitAsync(token);try{return await _probe.ProbeAsync(token);}finally{_probeGate.Release();}}
    private async Task WaitAsync(TimeSpan delay,CancellationToken token){using var linked=CancellationTokenSource.CreateLinkedTokenSource(token);var timer=Task.Delay(delay,linked.Token);var signal=_signal.WaitAsync(linked.Token);await Task.WhenAny(timer,signal);linked.Cancel();try{await Task.WhenAll(timer,signal);}catch(OperationCanceledException)when(!token.IsCancellationRequested){}token.ThrowIfCancellationRequested();}
    private void Signal(){try{if(_signal.CurrentCount==0)_signal.Release();}catch(SemaphoreFullException){}}
    private bool TryTransition(PowerGuardState next,DateTimeOffset now){if(_machine.TryTransition(next,now,out _))return true;System.Diagnostics.Debug.WriteLine($"Invalid PowerGuard transition: {_machine.State} -> {next}");_= _events.AppendAsync("InvariantFailure");return false;}
    private void InvalidateCountdown(){_countdownSession=null;_countdownDeadline=null;_epoch++;}
    private int Elapsed(DateTimeOffset now)=>SecondsBetween(_machine.StateSinceUtc,now);private static int SecondsBetween(DateTimeOffset from,DateTimeOffset to)=>Math.Max(0,(int)(to-from).TotalSeconds);
    private PowerGuardSnapshot CreateSnapshot(bool online,string? error=null)=>new(_machine.State,_settings.GuardEnabled,online,_lastOnline,_countdownDeadline is null?0:Math.Max(0,(int)Math.Ceiling((_countdownDeadline.Value-DateTimeOffset.UtcNow).TotalSeconds)),_settings.SuppressedUntilConnectivityRestored,error);
    private void Publish(bool online,string? error=null){Snapshot=CreateSnapshot(online,error);try{SnapshotChanged?.Invoke(this,Snapshot);}catch{}}
    private static string? SafeFailure(ConnectivityProbeResult result)=>result.Endpoints.FirstOrDefault()?.FailureCategory switch{"Timeout"=>"Timeout","RedirectRejected"=>"RedirectRejected","UnexpectedStatus"=>"UnexpectedStatus","UnexpectedResponse"=>"UnexpectedResponse","ResponseTooLarge"=>"ResponseTooLarge",_=>"Unavailable"};
    private async Task ClearSuppressionAfterRecoveryAsync(bool online,CancellationToken token){await _settingsGate.WaitAsync(token);try{var saved=await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=false,token);await _stateGate.WaitAsync(token);try{_settings=WithSuppression(_settings,saved.SuppressedUntilConnectivityRestored);Publish(online);}finally{_stateGate.Release();}}finally{_settingsGate.Release();}}
    private async Task ClearReservationAsync(Guid reservation){await _stateGate.WaitAsync(CancellationToken.None);try{if(_settingsReservation==reservation)_settingsReservation=null;}finally{_stateGate.Release();}}
    private static PowerGuardSettings WithSuppression(PowerGuardSettings source,bool value){var clone=PowerGuardSettingsStore.Clone(source);clone.SuppressedUntilConnectivityRestored=value;return clone;}
    private static bool SettingsAreValid(PowerGuardSettings s)=>s.StartupGraceSeconds is>=0 and<=600&&s.OfflineConfirmationSeconds is>=15 and<=300&&s.ShutdownCountdownSeconds is>=60 and<=3600&&s.RecoveryConfirmationSeconds is>=5 and<=120;
    private bool IsAcceptingOperations=>_active&&!_disposed;
    internal void StartForTesting(DateTimeOffset now){TryTransition(PowerGuardState.StartupGrace,now);_active=true;_lifetimeCts??=new();Publish(false);}internal void ResumeForTesting(DateTimeOffset now){InvalidateCountdown();_offlineSince=_recoverySince=null;if(_machine.State==PowerGuardState.ExecutingShutdown){TryTransition(PowerGuardState.Stopping,now);TryTransition(PowerGuardState.Disabled,now);}_active=true;TryTransition(PowerGuardState.StartupGrace,now);Publish(false);}
    internal Task<bool> EnterMonitoringFaultForTestingAsync(CancellationToken token=default)=>EnterMonitoringFaultAsync(_monitorGeneration,new InvalidOperationException("safe synthetic fault"),token);
    internal Task<bool> RecoverMonitoringForTestingAsync(CancellationToken token=default)=>RecoverMonitoringAsync(_monitorGeneration,token);
    private async Task StartCountdownPresentationAsync(Guid session,long epoch,int seconds,CancellationToken token)
    {
        using var linked=CreateOperationToken(token);await _presentationGate.WaitAsync(linked.Token);try
        {
            if(!await IsCountdownSessionValidAsync(session,epoch,linked.Token))return;
            await _warning.ShowRealAsync(seconds,linked.Token);
            if(!await IsCountdownSessionValidAsync(session,epoch,CancellationToken.None)){await _warning.CloseRealAsync(CancellationToken.None);return;}
            StartCountdownTicker(session,epoch);
        }finally{_presentationGate.Release();}
    }
    private async Task StopCountdownPresentationAsync(CancellationToken token)
    {await StopCountdownTickerAsync();await _presentationGate.WaitAsync(token);try{await _warning.CloseRealAsync(token);}finally{_presentationGate.Release();}}
    private async Task<bool> IsCountdownSessionValidAsync(Guid session,long epoch,CancellationToken token){await _stateGate.WaitAsync(token);try{return !_disposed&&_active&&_settings.GuardEnabled&&_machine.State==PowerGuardState.Countdown&&_countdownSession==session&&_epoch==epoch&&_settingsReservation is null;}finally{_stateGate.Release();}}
    private void StartCountdownTicker(Guid session,long epoch){if(_countdownTask is{IsCompleted:false}&&_tickerSession==session)return;_countdownCts?.Cancel();_countdownCts=new();_tickerSession=session;_countdownTask=CountdownTickerAsync(session,epoch,_countdownCts.Token);}
    private async Task CountdownTickerAsync(Guid session,long epoch,CancellationToken token){try{while(!token.IsCancellationRequested){int seconds;await _stateGate.WaitAsync(token);try{if(_countdownSession!=session||_epoch!=epoch||_machine.State!=PowerGuardState.Countdown)return;seconds=_countdownDeadline is null?0:Math.Max(0,(int)Math.Ceiling((_countdownDeadline.Value-DateTimeOffset.UtcNow).TotalSeconds));Publish(false);}finally{_stateGate.Release();}await _presentationGate.WaitAsync(token);try{if(!await IsCountdownSessionValidAsync(session,epoch,token))return;await _warning.UpdateRealAsync(seconds,token);}finally{_presentationGate.Release();}await Task.Delay(TimeSpan.FromSeconds(1),token);}}catch(OperationCanceledException)when(token.IsCancellationRequested){}}
    private async Task StopCountdownTickerAsync(){var cts=_countdownCts;var task=_countdownTask;_countdownCts=null;_countdownTask=null;_tickerSession=null;if(cts is null)return;cts.Cancel();if(task is not null)try{await task.WaitAsync(TimeSpan.FromSeconds(3));}catch(OperationCanceledException){}catch(TimeoutException){throw new PowerGuardCleanupException("PowerGuard countdown presentation did not stop safely.");}cts.Dispose();}
    private CancellationTokenSource CreateOperationToken(CancellationToken caller){var lifetime=_lifetimeCts?.Token??new CancellationToken(true);return CancellationTokenSource.CreateLinkedTokenSource(caller,lifetime);}
    private async Task SafeCloseAllAsync(){try{await _warning.CloseAllAsync();}catch(Exception e){System.Diagnostics.Debug.WriteLine(e.GetType().Name);}}
    public ValueTask DisposeAsync(){Task task;lock(_disposeSync){if(_disposed)return ValueTask.CompletedTask;if(_disposeTask is null||_disposeTask.IsFaulted||_disposeTask.IsCanceled)_disposeTask=DisposeCoreAsync();task=_disposeTask;}return new ValueTask(task);}
    private async Task DisposeCoreAsync(){await DeactivateAsync();if(_monitorTask is not null||_countdownTask is not null)throw new PowerGuardCleanupException("PowerGuard still owns running background tasks.");_disposed=true;Unsubscribe();SnapshotChanged=null;_events.ClearSubscribers();_lifetimeCts?.Dispose();_settingsGate.Dispose();_presentationGate.Dispose();_lifecycle.Dispose();_probeGate.Dispose();_signal.Dispose();_stateGate.Dispose();}
}
