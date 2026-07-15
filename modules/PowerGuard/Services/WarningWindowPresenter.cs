using System.Windows;
using System.Windows.Threading;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Modules.PowerGuard.ViewModels;
using QingToolbox.Modules.PowerGuard.Views;

namespace QingToolbox.Modules.PowerGuard.Services;
public enum WarningWindowSessionKind{None,RealWarning,TestPreview} public enum TestPreviewResult{Opened,ActivatedExisting,UnavailableDuringCountdown}

public sealed class WarningWindowPresenter(ILocalizationService localization,string moduleId):IWarningPresenter
{
    private readonly SemaphoreSlim _operations=new(1,1);private ShutdownWarningWindow? _real,_test;private Func<Task>? _suppress,_extend,_shutdown;
    public void Configure(Func<Task>suppress,Func<Task>extend,Func<Task>shutdown){_suppress=suppress;_extend=extend;_shutdown=shutdown;}
    public async Task ShowRealAsync(int seconds,CancellationToken token=default)=>await SerializedAsync(async()=>await DispatchAsync(()=>{CloseTestCore();if(_real is not null){_real.SetSeconds(seconds);_real.Activate();return;}var window=new ShutdownWarningWindow(localization,moduleId,new(){Seconds=seconds},()=>_suppress?.Invoke()??Task.CompletedTask,()=>_extend?.Invoke()??Task.CompletedTask,()=>_shutdown?.Invoke()??Task.CompletedTask);_real=window;window.Closed+=(_,_)=>{if(ReferenceEquals(_real,window))_real=null;};window.Show();window.PositionAtPrimaryWorkArea();},token),token);
    public async Task<TestPreviewResult> ShowTestAsync(int seconds,CancellationToken token=default)
    { TestPreviewResult result=TestPreviewResult.Opened;await SerializedAsync(async()=>await DispatchAsync(()=>{if(_real is not null){result=TestPreviewResult.UnavailableDuringCountdown;return;}if(_test is not null){_test.Activate();result=TestPreviewResult.ActivatedExisting;return;}var window=new ShutdownWarningWindow(localization,moduleId,new(){IsTestMode=true,Seconds=seconds},()=>CloseTestAsync(),()=>Task.CompletedTask,()=>CloseTestAsync());_test=window;window.Closed+=(_,_)=>{if(ReferenceEquals(_test,window))_test=null;};window.Show();window.PositionAtPrimaryWorkArea();},token),token);return result; }
    public async Task UpdateRealAsync(int seconds,CancellationToken token=default)=>await SerializedAsync(async()=>await DispatchAsync(()=>{if(_real is{IsLoaded:true})_real.SetSeconds(seconds);},token),token);
    public async Task CloseRealAsync(CancellationToken token=default)=>await SerializedAsync(async()=>await DispatchAsync(CloseRealCore,token),token);
    public async Task CloseTestAsync(CancellationToken token=default)=>await SerializedAsync(async()=>await DispatchAsync(CloseTestCore,token),token);
    public async Task CloseAllAsync(CancellationToken token=default)=>await SerializedAsync(async()=>await DispatchAsync(()=>{CloseTestCore();CloseRealCore();},token),token);
    public void RefreshLocalization()=>Observe(SerializedAsync(async()=>await DispatchAsync(()=>{if(_real is{IsLoaded:true})_real.RefreshLocalization();if(_test is{IsLoaded:true})_test.RefreshLocalization();},CancellationToken.None),CancellationToken.None));
    private void CloseRealCore(){var w=_real;_real=null;if(w is{IsLoaded:true})w.CloseWithoutCancel();}private void CloseTestCore(){var w=_test;_test=null;if(w is{IsLoaded:true})w.CloseWithoutCancel();}
    private async Task SerializedAsync(Func<Task> action,CancellationToken token){await _operations.WaitAsync(token);try{await action();}finally{_operations.Release();}}
    private static Task DispatchAsync(Action action,CancellationToken token){var d=Application.Current?.Dispatcher;if(d is null||d.HasShutdownStarted||d.HasShutdownFinished)return Task.CompletedTask;if(d.CheckAccess()){token.ThrowIfCancellationRequested();action();return Task.CompletedTask;}return d.InvokeAsync(action,DispatcherPriority.Normal,token).Task;}
    private static void Observe(Task task)=>_=task.ContinueWith(t=>System.Diagnostics.Debug.WriteLine(t.Exception?.GetBaseException().GetType().Name),CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
}
