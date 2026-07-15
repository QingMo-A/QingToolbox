using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Modules.PowerGuard.ViewModels;

namespace QingToolbox.Modules.PowerGuard.Views;

public partial class ShutdownWarningWindow : Window
{
    private readonly ILocalizationService _localization; private readonly string _moduleId;
    private readonly ShutdownWarningViewModel _viewModel; private readonly Func<Task> _cancel, _extend, _shutdown;
    private readonly DispatcherTimer? _testTimer; private bool _closeWithoutCancel;
    public ShutdownWarningWindow(ILocalizationService localization, string moduleId, ShutdownWarningViewModel viewModel,
        Func<Task> cancel, Func<Task> extend, Func<Task> shutdown)
    {
        InitializeComponent(); _localization=localization; _moduleId=moduleId; _viewModel=viewModel; _cancel=cancel; _extend=extend; _shutdown=shutdown; DataContext=viewModel;
        PreviewKeyDown += OnPreviewKeyDown; Closing += OnClosing; RefreshLocalization();
        if (viewModel.IsTestMode)
        {
            _testTimer = new() { Interval=TimeSpan.FromSeconds(1) };
            _testTimer.Tick += (_, _) => { if (_viewModel.Seconds > 0) _viewModel.Seconds--; else { _testTimer.Stop(); ModeText.Text=T("status.testCompleted","Test completed"); } };
            _testTimer.Start();
        }
    }
    private string T(string key,string fallback)=>_localization.GetModuleString(_moduleId,key,fallback);
    public void RefreshLocalization()
    {
        TitleText.Text=T("warning.title","Network connection interrupted");
        DescriptionText.Text=T(_viewModel.IsTestMode?"warning.testDescription":"warning.description",_viewModel.IsTestMode?"Test mode. No shutdown will occur.":"The computer will shut down normally if connectivity does not return.");
        ModeText.Text=_viewModel.IsTestMode?T("status.testMode","Test mode — no shutdown"):"PowerGuard";
        CancelMeaningText.Text=T("warning.cancelMeaning","Cancel suppresses protection only for this outage.");
        CancelButton.Content=T("actions.cancelCurrent","Cancel this shutdown"); ExtendButton.Content=T("actions.extendTenMinutes","Extend 10 minutes");
        ShutdownButton.Content=T(_viewModel.IsTestMode?"status.testCompleted":"actions.shutdownNow",_viewModel.IsTestMode?"Test completed":"Shut down now");
        ExtendButton.IsEnabled=!_viewModel.IsTestMode;
    }
    public void SetSeconds(int seconds)=>_viewModel.Seconds=seconds;
    public void PositionAtPrimaryWorkArea() { UpdateLayout(); var area=SystemParameters.WorkArea; Left=area.Left+20; Top=area.Bottom-ActualHeight-20; }
    public void CloseWithoutCancel() { _closeWithoutCancel=true; _testTimer?.Stop(); Close(); }
    private async void Cancel_Click(object sender,RoutedEventArgs e)=>await _cancel();
    private async void Extend_Click(object sender,RoutedEventArgs e)=>await _extend();
    private async void Shutdown_Click(object sender,RoutedEventArgs e)
    {
        if (_viewModel.IsTestMode) { await _shutdown(); return; }
        if (MessageBox.Show(T("warning.confirmShutdown","Shut down Windows now?"),"PowerGuard",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes) await _shutdown();
    }
    private async void OnPreviewKeyDown(object sender,KeyEventArgs e) { if(e.Key==Key.Escape){e.Handled=true;await _cancel();} }
    private void OnClosing(object? sender,CancelEventArgs e) { _testTimer?.Stop(); if(!_closeWithoutCancel){e.Cancel=true;_ = _cancel();} }
}
