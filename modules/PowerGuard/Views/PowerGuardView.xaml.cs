using System.Windows;
using System.Windows.Controls;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Modules.PowerGuard.ViewModels;
using QingToolbox.Modules.PowerGuard.Services;

namespace QingToolbox.Modules.PowerGuard.Views;
public partial class PowerGuardView : UserControl, ILocalizedModuleView, IDisposable
{
    private readonly ILocalizationService _localization; private readonly string _moduleId; private readonly PowerGuardViewModel _viewModel;
    public PowerGuardView(ILocalizationService localization,string moduleId,PowerGuardViewModel viewModel){InitializeComponent();_localization=localization;_moduleId=moduleId;_viewModel=viewModel;DataContext=viewModel;RefreshLocalization();}
    private string T(string key,string fallback)=>_localization.GetModuleString(_moduleId,key,fallback);
    public void RefreshLocalization()
    {
        TitleText.Text=T("view.title","PowerGuard"); SubtitleText.Text=T("view.subtitle","Offline shutdown protection for unattended computers."); GuardLabel.Text=T("view.guardStatus","Guard status"); StateLabel.Text=T("view.currentState","Current state"); CountdownLabel.Text=T("view.countdown","Countdown"); SettingsGroup.Header=T("view.settings","Settings"); RecentEventsGroup.Header=T("view.recentEvents","Recent events"); GuardCheck.Content=T("actions.enableGuard","Enable guard"); GraceLabel.Text=T("view.startupGrace","Startup grace (seconds)"); OfflineLabel.Text=T("view.offlineConfirmation","Offline confirmation (seconds)"); ShutdownLabel.Text=T("view.shutdownCountdown","Shutdown countdown (seconds)"); RecoveryLabel.Text=T("view.recoveryConfirmation","Recovery confirmation (seconds)"); SaveButton.Content=T("actions.save","Save"); ProbeButton.Content=T("actions.probeNow","Probe now"); TestButton.Content=T("actions.testWarning","Test warning"); CancelButton.Content=T("actions.cancelCurrent","Cancel current shutdown"); RearmButton.Content=T("actions.rearmCurrent","Rearm current outage"); NoticeText.Text=T("view.setupNotice","Enable QingToolbox login startup and 'Start with toolbox' for unattended operation. Closing this view does not stop the guard. UPS status is not read in this version.");
    }
    private async void Save_Click(object s,RoutedEventArgs e){try{await _viewModel.SaveAsync();StatusText.Text=T("status.settingsSaved","Settings saved.");}catch{StatusText.Text=T("errors.settingsSaveFailed","Settings could not be saved.");}}
    private async void Probe_Click(object s,RoutedEventArgs e){try{var r=await _viewModel.ProbeAsync();StatusText.Text=$"{(r.IsOnline?T("status.online","Online"):T("status.suspectedOffline","Offline"))} — {string.Join(", ",r.Endpoints.Select(x=>$"{x.Name}: {(x.Succeeded?"OK":x.FailureCategory)} ({x.ElapsedMilliseconds} ms)"))}";}catch{StatusText.Text=T("errors.probeFailed","Connectivity probe failed.");}}
    private async void Test_Click(object s,RoutedEventArgs e){try{var result=await _viewModel.TestAsync();if(result==TestPreviewResult.UnavailableDuringCountdown)StatusText.Text=T("status.testUnavailable","A real shutdown countdown is active.");}catch{StatusText.Text=T("errors.operationFailed","Operation failed.");}}
    private async void Cancel_Click(object s,RoutedEventArgs e){try{await _viewModel.SuppressAsync();}catch{StatusText.Text=T("errors.operationFailed","Operation failed.");}}
    private async void Rearm_Click(object s,RoutedEventArgs e){try{await _viewModel.RearmAsync();}catch{StatusText.Text=T("errors.operationFailed","Operation failed.");}}
    public void Dispose()=>_viewModel.Dispose();
}
