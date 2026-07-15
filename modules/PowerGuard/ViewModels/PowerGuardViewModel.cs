using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Modules.PowerGuard.Models;
using QingToolbox.Modules.PowerGuard.Services;

namespace QingToolbox.Modules.PowerGuard.ViewModels;

public sealed class PowerGuardViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly PowerGuardController _controller;
    private readonly ILocalizationService _localization;
    private readonly string _moduleId;
    private PowerGuardSnapshot _snapshot;
    public event PropertyChangedEventHandler? PropertyChanged;
    public PowerGuardSnapshot Snapshot { get => _snapshot; private set { _snapshot=value; Changed(); RefreshComputed(); } }
    public PowerGuardSettings EditableSettings { get; private set; }
    public ObservableCollection<string> RecentEvents { get; }=[];
    public string GuardStatusText => T(Snapshot.GuardEnabled?"status.enabled":"status.disabled",Snapshot.GuardEnabled?"Enabled":"Disabled");
    public string NetworkStatusText => T(Snapshot.IsOnline?"status.online":"status.offline",Snapshot.IsOnline?"Online":"Offline");
    public string StateText => T($"status.{Snapshot.State switch { State.PowerGuardState.StartupGrace=>"startupGrace",State.PowerGuardState.SuspectedOffline=>"suspectedOffline",State.PowerGuardState.Countdown=>"countdown",State.PowerGuardState.SuppressedForCurrentOutage=>"suppressed",State.PowerGuardState.Recovering=>"recovering",State.PowerGuardState.ExecutingShutdown=>"executingShutdown",State.PowerGuardState.ActionFailed=>"actionFailed",State.PowerGuardState.MonitoringFault=>"monitoringFault",State.PowerGuardState.Online=>"online",_=>"disabled" }}",Snapshot.State.ToString());
    public string Countdown => TimeSpan.FromSeconds(Math.Max(0,Snapshot.RemainingSeconds)).ToString(@"mm\:ss");
    public Visibility CountdownVisibility => Snapshot.State==State.PowerGuardState.Countdown?Visibility.Visible:Visibility.Collapsed;
    public string LastOnlineText => Snapshot.LastOnlineUtc?.ToLocalTime().ToString("g") ?? T("status.never","Never");
    public string DefaultActionText => T("view.normalShutdown","Normal shutdown");

    public PowerGuardViewModel(PowerGuardController controller,ILocalizationService localization,string moduleId)
    { _controller=controller;_localization=localization;_moduleId=moduleId;_snapshot=controller.Snapshot;EditableSettings=Clone(controller.Settings);controller.SnapshotChanged+=OnSnapshotChanged;controller.RecentEventsChanged+=OnEventsChanged;localization.CultureChanged+=OnCultureChanged;_=RefreshEventsAsync(); }
    private string T(string key,string fallback)=>_localization.GetModuleString(_moduleId,key,fallback);
    private void OnSnapshotChanged(object? sender,PowerGuardSnapshot snapshot)=>Dispatch(()=>Snapshot=snapshot);
    private void OnEventsChanged(object? sender,EventArgs e)=>_=RefreshEventsAsync();
    private void OnCultureChanged(object? sender,EventArgs e){RefreshComputed();_=RefreshEventsAsync();}
    private async Task RefreshEventsAsync(){var events=await _controller.ReadRecentEventsAsync();Dispatch(()=>{RecentEvents.Clear();foreach(var item in events.Reverse())RecentEvents.Add($"{item.TimestampUtc.ToLocalTime():g}  {T("event."+item.Type,item.Type)}{(string.IsNullOrWhiteSpace(item.Detail)?"":" — "+item.Detail)}");});}
    private static void Dispatch(Action action){var d=Application.Current?.Dispatcher;if(d is null||d.CheckAccess())action();else _=d.InvokeAsync(action);}
    private void RefreshComputed(){foreach(var n in new[]{nameof(GuardStatusText),nameof(NetworkStatusText),nameof(StateText),nameof(Countdown),nameof(CountdownVisibility),nameof(LastOnlineText),nameof(DefaultActionText)})Changed(n);}
    public async Task SaveAsync(){await _controller.SaveSettingsAsync(EditableSettings);EditableSettings=Clone(_controller.Settings);Changed(nameof(EditableSettings));}
    public Task<ConnectivityProbeResult> ProbeAsync()=>_controller.ProbeNowAsync();
    public Task<TestPreviewResult> TestAsync()=>_controller.ShowTestWarningAsync();
    public Task SuppressAsync()=>_controller.SuppressCurrentOutageAsync(); public Task RearmAsync()=>_controller.RearmAsync();
    private static PowerGuardSettings Clone(PowerGuardSettings s)=>new(){GuardEnabled=s.GuardEnabled,StartupGraceSeconds=s.StartupGraceSeconds,OfflineConfirmationSeconds=s.OfflineConfirmationSeconds,ShutdownCountdownSeconds=s.ShutdownCountdownSeconds,RecoveryConfirmationSeconds=s.RecoveryConfirmationSeconds,PowerAction=s.PowerAction,SuppressedUntilConnectivityRestored=s.SuppressedUntilConnectivityRestored,ShowRecoveryNotification=s.ShowRecoveryNotification};
    private void Changed([CallerMemberName]string? name=null)=>PropertyChanged?.Invoke(this,new(name));
    public void Dispose(){_controller.SnapshotChanged-=OnSnapshotChanged;_controller.RecentEventsChanged-=OnEventsChanged;_localization.CultureChanged-=OnCultureChanged;}
}
