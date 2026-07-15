using System.Windows;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Modules.PowerGuard.ViewModels;
using QingToolbox.Modules.PowerGuard.Views;

namespace QingToolbox.Modules.PowerGuard.Services;
public enum WarningWindowSessionKind{None,RealWarning,TestPreview}
public enum TestPreviewResult{Opened,ActivatedExisting,UnavailableDuringCountdown}

public sealed class WarningWindowPresenter(ILocalizationService localization,string moduleId):IWarningPresenter
{
    private ShutdownWarningWindow? _real,_test;private Func<Task>? _suppress,_extend,_shutdown;
    public WarningWindowSessionKind SessionKind=>_real is not null?WarningWindowSessionKind.RealWarning:_test is not null?WarningWindowSessionKind.TestPreview:WarningWindowSessionKind.None;
    public void Configure(Func<Task>suppress,Func<Task>extend,Func<Task>shutdown){_suppress=suppress;_extend=extend;_shutdown=shutdown;}
    public async Task ShowRealAsync(int seconds,CancellationToken token=default)
    {
        await CloseTestAsync(token);await DispatchAsync(()=>
        {
            if(_real is not null){_real.SetSeconds(seconds);_real.Activate();return;}
            var window=new ShutdownWarningWindow(localization,moduleId,new(){Seconds=seconds},async()=>{if(_suppress is null)throw new InvalidOperationException();await _suppress();},async()=>{if(_extend is null)throw new InvalidOperationException();await _extend();},async()=>{if(_shutdown is null)throw new InvalidOperationException();await _shutdown();});
            _real=window;window.Closed+=(_,_)=>{if(ReferenceEquals(_real,window))_real=null;};window.Show();window.PositionAtPrimaryWorkArea();
        },token);
    }
    public async Task<TestPreviewResult> ShowTestAsync(int seconds,CancellationToken token=default)
    {
        if(_real is not null)return TestPreviewResult.UnavailableDuringCountdown;
        var result=TestPreviewResult.Opened;await DispatchAsync(()=>
        {
            if(_real is not null){result=TestPreviewResult.UnavailableDuringCountdown;return;}
            if(_test is not null){_test.Activate();result=TestPreviewResult.ActivatedExisting;return;}
            var window=new ShutdownWarningWindow(localization,moduleId,new(){IsTestMode=true,Seconds=seconds},()=>CloseTestAsync(),()=>Task.CompletedTask,()=>CloseTestAsync());
            _test=window;window.Closed+=(_,_)=>{if(ReferenceEquals(_test,window))_test=null;};window.Show();window.PositionAtPrimaryWorkArea();
        },token);return result;
    }
    public Task UpdateRealAsync(int seconds,CancellationToken token=default)=>DispatchAsync(()=>_real?.SetSeconds(seconds),token);
    public Task CloseRealAsync(CancellationToken token=default)=>DispatchAsync(()=>{var w=_real;_real=null;w?.CloseWithoutCancel();},token);
    public Task CloseTestAsync(CancellationToken token=default)=>DispatchAsync(()=>{var w=_test;_test=null;w?.CloseWithoutCancel();},token);
    public async Task CloseAllAsync(CancellationToken token=default){await CloseTestAsync(token);await CloseRealAsync(token);}
    public void RefreshLocalization(){if(_real is not null)_=DispatchAsync(_real.RefreshLocalization,CancellationToken.None);if(_test is not null)_=DispatchAsync(_test.RefreshLocalization,CancellationToken.None);}
    private static Task DispatchAsync(Action action,CancellationToken token){var d=Application.Current?.Dispatcher;if(d is null||d.CheckAccess()){token.ThrowIfCancellationRequested();action();return Task.CompletedTask;}return d.InvokeAsync(action,System.Windows.Threading.DispatcherPriority.Normal,token).Task;}
}
