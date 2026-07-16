using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Modules.PowerGuard.Models;
using QingToolbox.Modules.PowerGuard.Services;
using QingToolbox.Modules.PowerGuard.State;

namespace QingToolbox.Modules.PowerGuard.ViewModels;

public sealed class PowerGuardViewModel:INotifyPropertyChanged,IDisposable
{
    private readonly PowerGuardController _controller;private readonly ILocalizationService _localization;private readonly string _moduleId;
    private readonly CancellationTokenSource _disposeCts=new();private readonly SemaphoreSlim _refreshGate=new(1,1);private PowerGuardSnapshot _snapshot;private int _busy,_refreshRequested,_refreshGeneration;private bool _disposed;
    public event PropertyChangedEventHandler? PropertyChanged;public ObservableCollection<string> RecentEvents{get;}=[];
    public PowerGuardSettings EditableSettings{get;private set;} public bool IsBusy=>Volatile.Read(ref _busy)!=0;public bool IsNotBusy=>!IsBusy;
    public PowerGuardSnapshot Snapshot{get=>_snapshot;private set{_snapshot=value;Changed();RefreshComputed();}}
    public string GuardStatusText=>T(Snapshot.GuardEnabled?"status.enabled":"status.disabled",Snapshot.GuardEnabled?"Enabled":"Disabled");
    public string NetworkStatusText=>T(Snapshot.IsOnline?"status.online":"status.offline",Snapshot.IsOnline?"Online":"Offline");
    public string StateText=>T("status."+(Snapshot.State switch{PowerGuardState.StartupGrace=>"startupGrace",PowerGuardState.SuspectedOffline=>"suspectedOffline",PowerGuardState.Countdown=>"countdown",PowerGuardState.SuppressedForCurrentOutage=>"suppressed",PowerGuardState.Recovering=>"recovering",PowerGuardState.ExecutingShutdown=>"executingShutdown",PowerGuardState.ActionFailed=>"actionFailed",PowerGuardState.MonitoringFault=>"monitoringFault",PowerGuardState.Stopping=>"stopping",PowerGuardState.Online=>"online",_=>"disabled"}),Snapshot.State.ToString());
    public string Countdown=>TimeSpan.FromSeconds(Math.Max(0,Snapshot.RemainingSeconds)).ToString(@"mm\:ss");public Visibility CountdownVisibility=>Snapshot.State==PowerGuardState.Countdown?Visibility.Visible:Visibility.Collapsed;
    public string LastOnlineText=>Snapshot.LastOnlineUtc?.ToLocalTime().ToString("g")??T("status.never","Never");public string DefaultActionText=>T("view.normalShutdown","Normal shutdown");
    public string LastProbeText=>$"{T("view.lastProbe","Last check")}: {Snapshot.LastProbeUtc?.ToLocalTime().ToString("g")??T("status.none","None")}";
    public string LastSuccessfulProbeText=>$"{T("view.lastSuccessfulProbe","Last online")}: {Snapshot.LastSuccessfulProbeUtc?.ToLocalTime().ToString("g")??T("status.none","None")}";
    public string ConsecutiveFailuresText=>$"{T("view.consecutiveFailures","Consecutive failures")}: {Snapshot.ConsecutiveProbeFailures}";
    public bool CanSave=>IsNotBusy&&Snapshot.CanChangeSettings;public bool CanProbe=>IsNotBusy&&Snapshot.CanProbeNow;public bool CanTest=>IsNotBusy&&Snapshot.CanTestWarning;
    public bool CanSuppress=>IsNotBusy&&Snapshot.CanSuppressCurrentOutage;public bool CanRearm=>IsNotBusy&&Snapshot.CanRearmCurrentOutage;

    public PowerGuardViewModel(PowerGuardController controller,ILocalizationService localization,string moduleId){_controller=controller;_localization=localization;_moduleId=moduleId;_snapshot=controller.Snapshot;EditableSettings=Clone(controller.Settings);controller.SnapshotChanged+=OnSnapshotChanged;controller.RecentEventsChanged+=OnEventsChanged;localization.CultureChanged+=OnCultureChanged;RequestEventRefresh();}
    private string T(string key,string fallback)=>_localization.GetModuleString(_moduleId,key,fallback);
    private void OnSnapshotChanged(object? s,PowerGuardSnapshot snapshot)=>Dispatch(()=>Snapshot=snapshot);private void OnEventsChanged(object? s,EventArgs e)=>RequestEventRefresh();private void OnCultureChanged(object? s,EventArgs e){RefreshComputed();RequestEventRefresh();}
    private void RequestEventRefresh(){if(_disposed)return;Interlocked.Exchange(ref _refreshRequested,1);var generation=Interlocked.Increment(ref _refreshGeneration);_=RefreshEventsLoopAsync(generation);}
    private async Task RefreshEventsLoopAsync(int generation){bool entered;try{entered=await _refreshGate.WaitAsync(0,_disposeCts.Token);}catch(OperationCanceledException){return;}if(!entered)return;try{while(Interlocked.Exchange(ref _refreshRequested,0)!=0&&!_disposed){IReadOnlyList<GuardEvent> items;try{items=await _controller.ReadRecentEventsAsync(_disposeCts.Token);}catch(OperationCanceledException)when(_disposeCts.IsCancellationRequested){return;}catch(Exception e){System.Diagnostics.Debug.WriteLine(e.GetType().Name);return;}var rendered=items.Reverse().Select(RenderEvent).ToArray();if(generation==Volatile.Read(ref _refreshGeneration))Dispatch(()=>{if(_disposed)return;RecentEvents.Clear();foreach(var item in rendered)RecentEvents.Add(item);});generation=Volatile.Read(ref _refreshGeneration);}}finally{_refreshGate.Release();}}
    private string RenderEvent(GuardEvent item){var text=T("event."+item.Type,item.Type);var detail=item.Detail switch{"Timeout"=>T("failure.timeout","Timed out"),"RedirectRejected"=>T("failure.redirect","Redirect rejected"),"UnexpectedStatus"=>T("failure.status","Unexpected response status"),"UnexpectedResponse"=>T("failure.content","Unexpected response content"),"ResponseTooLarge"=>T("failure.tooLarge","Response was too large"),"Unavailable"=>T("failure.unavailable","Endpoint unavailable"),_=>null};return $"{item.TimestampUtc.ToLocalTime():g}  {text}{(detail is null?"":" — "+detail)}";}
    private static void Dispatch(Action action){var d=Application.Current?.Dispatcher;if(d is null||d.CheckAccess())action();else _=d.InvokeAsync(action);}
    private void RefreshComputed(){foreach(var n in new[]{nameof(GuardStatusText),nameof(NetworkStatusText),nameof(StateText),nameof(Countdown),nameof(CountdownVisibility),nameof(LastOnlineText),nameof(DefaultActionText),nameof(LastProbeText),nameof(LastSuccessfulProbeText),nameof(ConsecutiveFailuresText),nameof(CanSave),nameof(CanProbe),nameof(CanTest),nameof(CanSuppress),nameof(CanRearm)})Changed(n);}
    private async Task<T> RunBusyAsync<T>(Func<Task<T>> action,T unavailable){if(Interlocked.CompareExchange(ref _busy,1,0)!=0)return unavailable;Changed(nameof(IsBusy));Changed(nameof(IsNotBusy));RefreshComputed();try{return await action();}finally{Interlocked.Exchange(ref _busy,0);Changed(nameof(IsBusy));Changed(nameof(IsNotBusy));RefreshComputed();}}
    public Task<GuardOperationResult> SaveAsync()=>RunBusyAsync(async()=>{var r=await _controller.SaveSettingsAsync(EditableSettings);EditableSettings=Clone(_controller.Settings);Changed(nameof(EditableSettings));return r;},GuardOperationResult.NotAvailable);
    public Task<(GuardOperationResult Result,ConnectivityProbeResult? Probe)> ProbeAsync()=>RunBusyAsync(()=>_controller.ProbeNowAsync(),(GuardOperationResult.NotAvailable,null));
    public Task<TestPreviewResult> TestAsync()=>RunBusyAsync(()=>_controller.ShowTestWarningAsync(),TestPreviewResult.UnavailableDuringCountdown);
    public Task<GuardOperationResult> SuppressAsync()=>RunBusyAsync(()=>_controller.SuppressCurrentOutageAsync(),GuardOperationResult.NotAvailable);public Task<GuardOperationResult> RearmAsync()=>RunBusyAsync(()=>_controller.RearmAsync(),GuardOperationResult.NotAvailable);
    public void ReloadSettings(){EditableSettings=Clone(_controller.Settings);Changed(nameof(EditableSettings));}
    private static PowerGuardSettings Clone(PowerGuardSettings s)=>new(){GuardEnabled=s.GuardEnabled,StartupGraceSeconds=s.StartupGraceSeconds,OfflineConfirmationSeconds=s.OfflineConfirmationSeconds,ShutdownCountdownSeconds=s.ShutdownCountdownSeconds,RecoveryConfirmationSeconds=s.RecoveryConfirmationSeconds,PowerAction=s.PowerAction,SuppressedUntilConnectivityRestored=s.SuppressedUntilConnectivityRestored,ShowRecoveryNotification=s.ShowRecoveryNotification};
    private void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
    public void Dispose(){if(_disposed)return;_disposed=true;_controller.SnapshotChanged-=OnSnapshotChanged;_controller.RecentEventsChanged-=OnEventsChanged;_localization.CultureChanged-=OnCultureChanged;_disposeCts.Cancel();_disposeCts.Dispose();}
}
