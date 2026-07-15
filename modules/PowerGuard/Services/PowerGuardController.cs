using System.Net.NetworkInformation;
using Microsoft.Win32;
using QingToolbox.Modules.PowerGuard.Models;
using QingToolbox.Modules.PowerGuard.State;

namespace QingToolbox.Modules.PowerGuard.Services;

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
    private readonly SemaphoreSlim _lifecycle=new(1,1),_probeGate=new(1,1),_signal=new(0,1),_stateGate=new(1,1);
    private CancellationTokenSource? _monitorCts,_countdownCts; private Task? _monitorTask,_countdownTask;
    private DateTimeOffset? _offlineSince,_recoverySince,_countdownDeadline,_lastOnline; private Guid? _countdownSession,_finalProbeSession;
    private PowerGuardSettings _settings; private bool _subscribed,_disposed,_active; private int _pendingResume,_monitorGeneration,_disposeStarted; private long _epoch;
    private readonly TimeSpan _faultBackoff;
    public PowerGuardSettings Settings=>PowerGuardSettingsStore.Clone(_settings); public PowerGuardSnapshot Snapshot{get;private set;}
    public event EventHandler<PowerGuardSnapshot>? SnapshotChanged; public event EventHandler? RecentEventsChanged{add=>_events.EventAppended+=value;remove=>_events.EventAppended-=value;}
    public Task<IReadOnlyList<GuardEvent>> ReadRecentEventsAsync(CancellationToken token=default)=>_events.ReadRecentAsync(20,token);

    public PowerGuardController(IConnectivityProbe probe,IPowerActionService power,PowerGuardSettingsStore settingsStore,PowerGuardEventStore events,IWarningPresenter warning,PowerGuardSettings settings,TimeSpan? faultBackoff=null)
    { _probe=probe;_power=power;_settingsStore=settingsStore;_events=events;_warning=warning;_settings=PowerGuardSettingsStore.Clone(settings);_faultBackoff=faultBackoff??TimeSpan.FromSeconds(10);Snapshot=CreateSnapshot(false); }

    public async Task ActivateAsync(CancellationToken token=default)
    {
        await _lifecycle.WaitAsync(token); try
        {
            if(_disposed)throw new ObjectDisposedException(nameof(PowerGuardController)); if(_monitorTask is {IsCompleted:false})return;
            if(_monitorTask is not null){try{await _monitorTask;}catch(Exception e){System.Diagnostics.Debug.WriteLine(e.GetType().Name);}_monitorCts?.Dispose();}
            token.ThrowIfCancellationRequested(); _active=true; var generation=++_monitorGeneration; _monitorCts=new();
            await _stateGate.WaitAsync(token);try{Transition(_settings.GuardEnabled?PowerGuardState.StartupGrace:PowerGuardState.Disabled,DateTimeOffset.UtcNow);Publish(false);}finally{_stateGate.Release();}
            if(_settings.GuardEnabled)Subscribe(); _monitorTask=MonitorSupervisorAsync(generation,_monitorCts.Token); await _events.AppendAsync("Activated",token:token);
        } finally{_lifecycle.Release();}
    }

    public async Task DeactivateAsync(CancellationToken token=default)
    {
        await _lifecycle.WaitAsync(token); try
        {
            _active=false;++_monitorGeneration; CancellationTokenSource? cts;Task? task;
            await _stateGate.WaitAsync(token);try{if(_machine.State!=PowerGuardState.Disabled)Transition(PowerGuardState.Stopping,DateTimeOffset.UtcNow);InvalidateCountdown();Publish(false);cts=_monitorCts;task=_monitorTask;}finally{_stateGate.Release();}
            cts?.Cancel();Unsubscribe(); if(task is not null)try{await task.WaitAsync(TimeSpan.FromSeconds(15));}catch(Exception e){System.Diagnostics.Debug.WriteLine(e.GetType().Name);}
            await StopCountdownTickerAsync();await SafeCloseAllAsync();
            await _stateGate.WaitAsync(CancellationToken.None);try{_offlineSince=_recoverySince=_countdownDeadline=null;Transition(PowerGuardState.Disabled,DateTimeOffset.UtcNow);Publish(false);_monitorTask=null;_monitorCts=null;}finally{_stateGate.Release();}
            cts?.Dispose();await _events.AppendAsync("Deactivated",token:token);
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
        await _stateGate.WaitAsync(token);try{if(!IsCurrentMonitor(generation)||_machine.State==PowerGuardState.Stopping)return false;Transition(PowerGuardState.MonitoringFault,DateTimeOffset.UtcNow);_offlineSince=_recoverySince=null;InvalidateCountdown();Publish(false,"MonitoringFault");}finally{_stateGate.Release();}
        await StopCountdownTickerAsync();await _warning.CloseRealAsync(token);await _events.AppendAsync("MonitoringFault",token:token);System.Diagnostics.Debug.WriteLine(exception.GetType().Name);return true;
    }
    private async Task<bool> RecoverMonitoringAsync(int generation,CancellationToken token)
    {
        await _stateGate.WaitAsync(token);try{if(!IsCurrentMonitor(generation)||_machine.State!=PowerGuardState.MonitoringFault)return false;Transition(_settings.GuardEnabled?PowerGuardState.StartupGrace:PowerGuardState.Disabled,DateTimeOffset.UtcNow);Publish(false);}finally{_stateGate.Release();}
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
        bool close=false,show=false,clearSuppression=false;Guid? finalSession=null;string eventName=result.IsOnline?"ProbeSucceeded":"ProbeFailed";
        await _stateGate.WaitAsync(token);try
        {
            if(_machine.State is PowerGuardState.Stopping or PowerGuardState.Disabled or PowerGuardState.MonitoringFault)return;
            if(result.IsOnline)
            {
                _lastOnline=now;_offlineSince=null;
                if(_machine.State is PowerGuardState.Countdown or PowerGuardState.SuspectedOffline or PowerGuardState.ActionFailed){_recoverySince=now;InvalidateCountdown();Transition(PowerGuardState.Recovering,now);close=true;}
                if(_machine.State==PowerGuardState.SuppressedForCurrentOutage)_recoverySince??=now;
                if((_machine.State==PowerGuardState.StartupGrace&&Elapsed(now)>=_settings.StartupGraceSeconds)||((_machine.State is PowerGuardState.Recovering or PowerGuardState.SuppressedForCurrentOutage)&&_recoverySince is{} recovery&&SecondsBetween(recovery,now)>=_settings.RecoveryConfirmationSeconds)){_recoverySince=null;Transition(PowerGuardState.Online,now);clearSuppression=true;eventName="ConnectivityRecovered";}
            }
            else
            {
                _recoverySince=null;
                if(_settings.SuppressedUntilConnectivityRestored||_machine.State==PowerGuardState.SuppressedForCurrentOutage)Transition(PowerGuardState.SuppressedForCurrentOutage,now);
                else if(!(_machine.State==PowerGuardState.StartupGrace&&Elapsed(now)<_settings.StartupGraceSeconds))
                {
                    if(_machine.State is PowerGuardState.Online or PowerGuardState.StartupGrace or PowerGuardState.Recovering){_offlineSince=now;Transition(PowerGuardState.SuspectedOffline,now);eventName="OfflineSuspected";}
                    if(_machine.State==PowerGuardState.SuspectedOffline&&_offlineSince is{} offline&&SecondsBetween(offline,now)>=_settings.OfflineConfirmationSeconds){_countdownDeadline=now.AddSeconds(_settings.ShutdownCountdownSeconds);_countdownSession=Guid.NewGuid();_epoch++;Transition(PowerGuardState.Countdown,now);show=true;eventName="CountdownStarted";}
                    if(_machine.State==PowerGuardState.Countdown&&_countdownDeadline<=now)finalSession=_countdownSession;
                }
            }
            Publish(result.IsOnline);
        }finally{_stateGate.Release();}
        if(clearSuppression){await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=false,token);await _stateGate.WaitAsync(token);try{_settings.SuppressedUntilConnectivityRestored=false;Publish(result.IsOnline);}finally{_stateGate.Release();}}
        if(close){await StopCountdownTickerAsync();await _warning.CloseRealAsync(token);} if(show){await _warning.ShowRealAsync(_settings.ShutdownCountdownSeconds,token);StartCountdownTicker();}
        await _events.AppendAsync(eventName,result.IsOnline?null:SafeFailure(result),token); if(finalSession is{} id)await ExecuteAfterFinalProbeAsync(id,token);
    }

    private async Task ExecuteAfterFinalProbeAsync(Guid session,CancellationToken token)
    {
        long finalEpoch;await _stateGate.WaitAsync(token);try{if(!_active||!_settings.GuardEnabled||_machine.State!=PowerGuardState.Countdown||_countdownSession!=session||_finalProbeSession is not null)return;_finalProbeSession=session;finalEpoch=_epoch;}finally{_stateGate.Release();}
        var final=await ProbeSerializedAsync(token);bool execute=false,recover=false;
        await _stateGate.WaitAsync(token);try
        {
            if(_finalProbeSession==session)_finalProbeSession=null;
            if(_machine.State!=PowerGuardState.Countdown||_countdownSession!=session||_epoch!=finalEpoch||!_settings.GuardEnabled||!_active)return;
            if(final.IsOnline){Transition(PowerGuardState.Recovering,DateTimeOffset.UtcNow);_recoverySince=DateTimeOffset.UtcNow;_countdownDeadline=null;recover=true;}else{Transition(PowerGuardState.ExecutingShutdown,DateTimeOffset.UtcNow);_countdownDeadline=null;execute=true;}Publish(final.IsOnline);
        }finally{_stateGate.Release();}
        await StopCountdownTickerAsync();await _warning.CloseRealAsync(token);
        if(recover)return;if(!execute)return;await _events.AppendAsync("ShutdownRequested",token:token);var result=await _power.RequestNormalShutdownAsync(token);
        if(result==PowerActionResult.Failed){await _stateGate.WaitAsync(token);try{if(_machine.State==PowerGuardState.ExecutingShutdown){Transition(PowerGuardState.ActionFailed,DateTimeOffset.UtcNow);Publish(false,"ShutdownFailed");}}finally{_stateGate.Release();}await _events.AppendAsync("ShutdownFailed",token:token);}
    }
    public async Task<GuardOperationResult> SuppressCurrentOutageAsync(CancellationToken token=default)
    {
        DateTimeOffset? deadline;await _stateGate.WaitAsync(token);try{if(!Snapshot.CanSuppressCurrentOutage||_countdownSession is null)return GuardOperationResult.NotAvailable;deadline=_countdownDeadline;_countdownSession=null;_epoch++;Publish(false);}finally{_stateGate.Release();}
        PowerGuardSettings saved;try{saved=await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=true,token);}catch{await _stateGate.WaitAsync(CancellationToken.None);try{if(_machine.State==PowerGuardState.Countdown&&_countdownSession is null){_countdownDeadline=deadline;_countdownSession=Guid.NewGuid();_epoch++;Publish(false);}}finally{_stateGate.Release();}await StopCountdownTickerAsync();StartCountdownTicker();throw;}
        var accepted=false;await _stateGate.WaitAsync(token);try{if(_machine.State==PowerGuardState.Countdown&&_countdownSession is null){_settings.SuppressedUntilConnectivityRestored=saved.SuppressedUntilConnectivityRestored;_countdownDeadline=null;Transition(PowerGuardState.SuppressedForCurrentOutage,DateTimeOffset.UtcNow);Publish(false);accepted=true;}}finally{_stateGate.Release();}
        if(!accepted){await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=false,token);return GuardOperationResult.NotAvailable;}
        await StopCountdownTickerAsync();await _warning.CloseRealAsync(token);await _events.AppendAsync("OutageSuppressed",token:token);return GuardOperationResult.Succeeded;
    }
    public async Task<GuardOperationResult> RearmAsync(CancellationToken token=default)
    {
        await _stateGate.WaitAsync(token);try{if(!Snapshot.CanRearmCurrentOutage)return GuardOperationResult.NotAvailable;_offlineSince=DateTimeOffset.UtcNow;Transition(PowerGuardState.SuspectedOffline,DateTimeOffset.UtcNow);Publish(false);}finally{_stateGate.Release();}
        await _settingsStore.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=false,token);await _stateGate.WaitAsync(token);try{_settings.SuppressedUntilConnectivityRestored=false;Publish(false);}finally{_stateGate.Release();}return GuardOperationResult.Succeeded;
    }
    public async Task<GuardOperationResult> ExtendCountdownAsync(CancellationToken token=default)
    {
        await _stateGate.WaitAsync(token);try{if(!Snapshot.CanExtendCountdown||_countdownDeadline is null||_countdownSession is null)return GuardOperationResult.NotAvailable;_countdownDeadline=_countdownDeadline.Value.AddMinutes(10);_countdownSession=Guid.NewGuid();_epoch++;Publish(false);}finally{_stateGate.Release();}await _events.AppendAsync("CountdownExtended",token:token);return GuardOperationResult.Succeeded;
    }
    public async Task<(GuardOperationResult Result,ConnectivityProbeResult? Probe)> ProbeNowAsync(CancellationToken token=default){if(!Snapshot.CanProbeNow)return(GuardOperationResult.NotAvailable,null);return(GuardOperationResult.Succeeded,await ProbeSerializedAsync(token));}
    public async Task<TestPreviewResult> ShowTestWarningAsync(CancellationToken token=default)=>Snapshot.CanTestWarning?await _warning.ShowTestAsync(30,token):TestPreviewResult.UnavailableDuringCountdown;
    public async Task<GuardOperationResult> ShutdownNowAsync(CancellationToken token=default)
    {
        await _stateGate.WaitAsync(token);try{if(!Snapshot.CanRequestShutdownNow)return GuardOperationResult.NotAvailable;InvalidateCountdown();Transition(PowerGuardState.ExecutingShutdown,DateTimeOffset.UtcNow);Publish(false);}finally{_stateGate.Release();}
        await StopCountdownTickerAsync();await _warning.CloseRealAsync(token);await _events.AppendAsync("ShutdownRequested","Manual",token);var r=await _power.RequestNormalShutdownAsync(token);if(r!=PowerActionResult.Failed)return GuardOperationResult.Succeeded;
        await _stateGate.WaitAsync(token);try{Transition(PowerGuardState.ActionFailed,DateTimeOffset.UtcNow);Publish(false,"ShutdownFailed");}finally{_stateGate.Release();}await _events.AppendAsync("ShutdownFailed",token:token);return GuardOperationResult.Failed;
    }
    public async Task<GuardOperationResult> SaveSettingsAsync(PowerGuardSettings settings,CancellationToken token=default)
    {
        if(!Snapshot.CanChangeSettings)return GuardOperationResult.NotAvailable;if(!SettingsAreValid(settings))return GuardOperationResult.Failed;var saved=await _settingsStore.UpdateAsync(s=>{s.GuardEnabled=settings.GuardEnabled;s.StartupGraceSeconds=settings.StartupGraceSeconds;s.OfflineConfirmationSeconds=settings.OfflineConfirmationSeconds;s.ShutdownCountdownSeconds=settings.ShutdownCountdownSeconds;s.RecoveryConfirmationSeconds=settings.RecoveryConfirmationSeconds;s.ShowRecoveryNotification=settings.ShowRecoveryNotification;},token);
        bool close=false;await _stateGate.WaitAsync(token);try{if(!Snapshot.CanChangeSettings)return GuardOperationResult.NotAvailable;_settings=saved;if(!saved.GuardEnabled){InvalidateCountdown();_offlineSince=_recoverySince=null;Transition(PowerGuardState.Disabled,DateTimeOffset.UtcNow);close=true;}else if(_machine.State==PowerGuardState.Disabled)Transition(PowerGuardState.StartupGrace,DateTimeOffset.UtcNow);Publish(false);}finally{_stateGate.Release();}
        if(close){Unsubscribe();await StopCountdownTickerAsync();await _warning.CloseRealAsync(token);}else Subscribe();await _events.AppendAsync("SettingsChanged",token:token);Signal();return GuardOperationResult.Succeeded;
    }

    private async Task ApplyResumeAsync(CancellationToken token){await _stateGate.WaitAsync(token);try{if(!_active||_machine.State==PowerGuardState.Stopping)return;InvalidateCountdown();_offlineSince=_recoverySince=null;Transition(_settings.GuardEnabled?PowerGuardState.StartupGrace:PowerGuardState.Disabled,DateTimeOffset.UtcNow);Publish(false);}finally{_stateGate.Release();}await StopCountdownTickerAsync();await _warning.CloseRealAsync(token);await _events.AppendAsync("ResumeGraceStarted",token:token);}
    private void Subscribe(){if(_subscribed)return;NetworkChange.NetworkAvailabilityChanged+=OnNetworkAvailabilityChanged;SystemEvents.PowerModeChanged+=OnPowerModeChanged;_subscribed=true;}
    private void Unsubscribe(){if(!_subscribed)return;NetworkChange.NetworkAvailabilityChanged-=OnNetworkAvailabilityChanged;SystemEvents.PowerModeChanged-=OnPowerModeChanged;_subscribed=false;}
    private void OnNetworkAvailabilityChanged(object? s,NetworkAvailabilityEventArgs e)=>Signal(); private void OnPowerModeChanged(object s,PowerModeChangedEventArgs e){if(e.Mode==PowerModes.Resume){Interlocked.Exchange(ref _pendingResume,1);Signal();}}
    private async Task<ConnectivityProbeResult> ProbeSerializedAsync(CancellationToken token){await _probeGate.WaitAsync(token);try{return await _probe.ProbeAsync(token);}finally{_probeGate.Release();}}
    private async Task WaitAsync(TimeSpan delay,CancellationToken token){using var linked=CancellationTokenSource.CreateLinkedTokenSource(token);var timer=Task.Delay(delay,linked.Token);var signal=_signal.WaitAsync(linked.Token);await Task.WhenAny(timer,signal);linked.Cancel();try{await Task.WhenAll(timer,signal);}catch(OperationCanceledException)when(!token.IsCancellationRequested){}token.ThrowIfCancellationRequested();}
    private void Signal(){try{if(_signal.CurrentCount==0)_signal.Release();}catch(SemaphoreFullException){}}
    private bool Transition(PowerGuardState next,DateTimeOffset now){if(_machine.TryTransition(next,now,out _))return true;System.Diagnostics.Debug.WriteLine($"Invalid PowerGuard transition: {_machine.State} -> {next}");_= _events.AppendAsync("InvariantFailure");return false;}
    private void InvalidateCountdown(){_countdownSession=null;_countdownDeadline=null;_epoch++;}
    private int Elapsed(DateTimeOffset now)=>SecondsBetween(_machine.StateSinceUtc,now);private static int SecondsBetween(DateTimeOffset from,DateTimeOffset to)=>Math.Max(0,(int)(to-from).TotalSeconds);
    private PowerGuardSnapshot CreateSnapshot(bool online,string? error=null)=>new(_machine.State,_settings.GuardEnabled,online,_lastOnline,_countdownDeadline is null?0:Math.Max(0,(int)Math.Ceiling((_countdownDeadline.Value-DateTimeOffset.UtcNow).TotalSeconds)),_settings.SuppressedUntilConnectivityRestored,error);
    private void Publish(bool online,string? error=null){Snapshot=CreateSnapshot(online,error);try{SnapshotChanged?.Invoke(this,Snapshot);}catch{}}
    private static string? SafeFailure(ConnectivityProbeResult result)=>result.Endpoints.FirstOrDefault()?.FailureCategory switch{"Timeout"=>"Timeout","RedirectRejected"=>"RedirectRejected","UnexpectedStatus"=>"UnexpectedStatus","UnexpectedResponse"=>"UnexpectedResponse","ResponseTooLarge"=>"ResponseTooLarge",_=>"Unavailable"};
    private static bool SettingsAreValid(PowerGuardSettings s)=>s.StartupGraceSeconds is>=0 and<=600&&s.OfflineConfirmationSeconds is>=15 and<=300&&s.ShutdownCountdownSeconds is>=60 and<=3600&&s.RecoveryConfirmationSeconds is>=5 and<=120;
    internal void StartForTesting(DateTimeOffset now){Transition(PowerGuardState.StartupGrace,now);_active=true;Publish(false);}internal void ResumeForTesting(DateTimeOffset now){InvalidateCountdown();_offlineSince=_recoverySince=null;if(_machine.State==PowerGuardState.ExecutingShutdown){Transition(PowerGuardState.Stopping,now);Transition(PowerGuardState.Disabled,now);}_active=true;Transition(PowerGuardState.StartupGrace,now);Publish(false);}
    internal Task<bool> EnterMonitoringFaultForTestingAsync(CancellationToken token=default)=>EnterMonitoringFaultAsync(_monitorGeneration,new InvalidOperationException("safe synthetic fault"),token);
    internal Task<bool> RecoverMonitoringForTestingAsync(CancellationToken token=default)=>RecoverMonitoringAsync(_monitorGeneration,token);
    private void StartCountdownTicker(){if(_countdownTask is{IsCompleted:false})return;_countdownCts=new();_countdownTask=CountdownTickerAsync(_countdownCts.Token);}
    private async Task CountdownTickerAsync(CancellationToken token){try{while(!token.IsCancellationRequested){int seconds;Guid? session;await _stateGate.WaitAsync(token);try{session=_countdownSession;seconds=_countdownDeadline is null?0:Math.Max(0,(int)Math.Ceiling((_countdownDeadline.Value-DateTimeOffset.UtcNow).TotalSeconds));Publish(false);}finally{_stateGate.Release();}if(session is null)return;await _warning.UpdateRealAsync(seconds,token);await Task.Delay(TimeSpan.FromSeconds(1),token);}}catch(OperationCanceledException)when(token.IsCancellationRequested){}}
    private async Task StopCountdownTickerAsync(){var cts=_countdownCts;var task=_countdownTask;_countdownCts=null;_countdownTask=null;if(cts is null)return;cts.Cancel();if(task is not null)try{await task.WaitAsync(TimeSpan.FromSeconds(3));}catch(Exception e)when(e is OperationCanceledException or TimeoutException){}cts.Dispose();}
    private async Task SafeCloseAllAsync(){try{await _warning.CloseAllAsync();}catch(Exception e){System.Diagnostics.Debug.WriteLine(e.GetType().Name);}}
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref _disposeStarted,1)!=0)return;await DeactivateAsync();_disposed=true;Unsubscribe();SnapshotChanged=null;_events.ClearSubscribers();_lifecycle.Dispose();_probeGate.Dispose();_signal.Dispose();_stateGate.Dispose();}
}
