using QingToolbox.Modules.PowerGuard.Models;
using QingToolbox.Modules.PowerGuard.Services;
using QingToolbox.Modules.PowerGuard.State;
using QingToolbox.ModuleLoader;
using QingToolbox.Core.Runtime;
using QingToolbox.Shell.Services;
using QingToolbox.Abstractions.Localization;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;

var root=Path.Combine(Path.GetTempPath(),$"PowerGuard-smoke-{Guid.NewGuid():N}");Directory.CreateDirectory(root);
try
{
 var store=new PowerGuardSettingsStore(root);var events=new PowerGuardEventStore(root);var probe=new FakeProbe();var power=new FakePower();var warning=new FakeWarning();
 var settings=new PowerGuardSettings{StartupGraceSeconds=0,OfflineConfirmationSeconds=60,ShutdownCountdownSeconds=600,RecoveryConfirmationSeconds=30};settings.Normalize();
 await using var controller=new PowerGuardController(probe,power,store,events,warning,settings);
 var start=DateTimeOffset.UtcNow;controller.StartForTesting(start);Require(controller.Snapshot.State==PowerGuardState.StartupGrace,"Activation must enter startup grace.");
 await controller.ProcessProbeAsync(Offline(start),start);Require(controller.Snapshot.State==PowerGuardState.SuspectedOffline,"Offline candidate must be suspected first.");
 await controller.ProcessProbeAsync(Online(start.AddSeconds(10)),start.AddSeconds(10));Require(controller.Snapshot.State==PowerGuardState.Recovering,"Brief outage recovery must not start countdown.");
 await controller.ProcessProbeAsync(Online(start.AddSeconds(40)),start.AddSeconds(40));Require(controller.Snapshot.State==PowerGuardState.Online,"Stable recovery must return online.");
 await controller.ProcessProbeAsync(Offline(start.AddSeconds(50)),start.AddSeconds(50));await controller.ProcessProbeAsync(Offline(start.AddSeconds(110)),start.AddSeconds(110));Require(controller.Snapshot.State==PowerGuardState.Countdown&&warning.ShowCount==1,"Confirmed outage must start one countdown.");
 Require(await controller.EnterMonitoringFaultForTestingAsync(),"Synthetic monitoring fault must be accepted.");Require(controller.Snapshot.State==PowerGuardState.MonitoringFault&&controller.Snapshot.RemainingSeconds==0,"Monitoring fault must invalidate the deadline.");Require(warning.CloseCount>0,"Monitoring fault must stop the ticker and close the real warning.");Require(await controller.RecoverMonitoringForTestingAsync()&&controller.Snapshot.State==PowerGuardState.StartupGrace,"Monitoring fault recovery must use a fresh startup grace period.");
 var restarted=DateTimeOffset.UtcNow;await controller.ProcessProbeAsync(Offline(restarted),restarted);await controller.ProcessProbeAsync(Offline(restarted.AddSeconds(60)),restarted.AddSeconds(60));
 var extension=await controller.ExtendCountdownAsync();Require(extension==GuardOperationResult.Succeeded&&controller.Snapshot.RemainingSeconds>600,$"Extension must add ten minutes (state={controller.Snapshot.State}, remaining={controller.Snapshot.RemainingSeconds}).");
 await controller.SuppressCurrentOutageAsync();Require(controller.Snapshot.State==PowerGuardState.SuppressedForCurrentOutage&&warning.CloseCount>0,"Cancel must suppress and close warning.");
 var persisted=await store.ReadAsync();Require(persisted.SuppressedUntilConnectivityRestored,"Suppression must persist.");
 await controller.ProcessProbeAsync(Offline(start.AddMinutes(20)),start.AddMinutes(20));Require(warning.ShowCount==2&&power.Count==0,"Suppressed outage must not warn or shut down.");
 await controller.ProcessProbeAsync(Online(start.AddMinutes(21)),start.AddMinutes(21));await controller.ProcessProbeAsync(Online(start.AddMinutes(21).AddSeconds(30)),start.AddMinutes(21).AddSeconds(30));Require(!controller.Settings.SuppressedUntilConnectivityRestored&&controller.Snapshot.State==PowerGuardState.Online,"Stable recovery must clear suppression and rearm.");
 await controller.ProcessProbeAsync(Offline(start.AddMinutes(22)),start.AddMinutes(22));await controller.ProcessProbeAsync(Offline(start.AddMinutes(23)),start.AddMinutes(23));
 probe.Results.Enqueue(Offline(start.AddMinutes(33)));await controller.ProcessProbeAsync(Offline(start.AddMinutes(33)),start.AddMinutes(33));Require(power.Count==1&&controller.Snapshot.State==PowerGuardState.ExecutingShutdown,"Expired offline countdown must request one normal shutdown after a final offline probe.");
 await controller.ProcessProbeAsync(Offline(start.AddMinutes(34)),start.AddMinutes(34));Require(power.Count==1,"Power action must not repeat.");
 controller.ResumeForTesting(start.AddMinutes(22));Require(controller.Snapshot.State==PowerGuardState.StartupGrace,"Resume must restart grace.");
 var beforeTestPower=power.Count;await controller.ShowTestWarningAsync();Require(power.Count==beforeTestPower&&warning.TestShowCount==1,"Test warning must never invoke power action.");

 var machine=new PowerGuardStateMachine();Require(!machine.TryTransition(PowerGuardState.ExecutingShutdown,DateTimeOffset.UtcNow,out _),"Illegal state transitions must be rejected.");Require(machine.State==PowerGuardState.Disabled,"Rejected transition must not mutate state.");
 Require(await controller.SuppressCurrentOutageAsync()==GuardOperationResult.NotAvailable,"Cancel must be rejected outside Countdown.");Require(await controller.RearmAsync()==GuardOperationResult.NotAvailable,"Rearm must be rejected outside Suppressed state.");
 var launcher=new RetryLauncher();var powerService=new PowerActionService(launcher);Require(await powerService.RequestNormalShutdownAsync()==PowerActionResult.Failed,"A failed launch must be reported.");Require(await powerService.RequestNormalShutdownAsync()==PowerActionResult.Started,"A failed launch must remain retriable.");Require(await powerService.RequestNormalShutdownAsync()==PowerActionResult.AlreadyRequested,"A successful request must be latched.");
 await Task.WhenAll(store.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=true),store.UpdateAsync(s=>s.ShowRecoveryNotification=false));var merged=await store.ReadAsync();Require(merged.SuppressedUntilConnectivityRestored&&!merged.ShowRecoveryNotification,"Concurrent partial settings updates must not overwrite one another.");
 await VerifyStaleFinalProbeAsync(root);await VerifySupervisorRecoveryAsync(root);await VerifyOfflineHttpProbeAsync();

 await File.WriteAllTextAsync(Path.Combine(root,"settings.json"),"{ invalid");var safe=await store.ReadAsync();Require(safe.GuardEnabled&&Directory.GetFiles(root,"settings.corrupt-*.json").Length==1,"Corrupt settings must use defaults and be backed up.");
 Console.WriteLine("PowerGuard smoke test passed: state, suppression, recovery, extension, resume, safe settings and no real power action.");
 store.Dispose();events.Dispose();
}
finally{if(Directory.Exists(root))Directory.Delete(root,true);}

if(args.Length==2&&args[0]=="--package") await VerifyPackageAsync(args[1]);

static ConnectivityProbeResult Online(DateTimeOffset now)=>new(true,true,[new("fake",true,1,"")],now);
static ConnectivityProbeResult Offline(DateTimeOffset now)=>new(false,false,[new("fake",false,1,"Offline")],now);
static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
static async Task VerifyStaleFinalProbeAsync(string parent)
{
 var root=Path.Combine(parent,"race");Directory.CreateDirectory(root);var blocker=new BlockingProbe();var power=new FakePower();var warning=new FakeWarning();using var store=new PowerGuardSettingsStore(root);using var events=new PowerGuardEventStore(root);
 var settings=new PowerGuardSettings{StartupGraceSeconds=0,OfflineConfirmationSeconds=15,ShutdownCountdownSeconds=60,RecoveryConfirmationSeconds=5};await using var controller=new PowerGuardController(blocker,power,store,events,warning,settings);var now=DateTimeOffset.UtcNow;controller.StartForTesting(now);
 await controller.ProcessProbeAsync(Offline(now),now);await controller.ProcessProbeAsync(Offline(now.AddSeconds(15)),now.AddSeconds(15));Require(controller.Snapshot.State==PowerGuardState.Countdown,"Race test must enter Countdown.");
 var final=controller.ProcessProbeAsync(Offline(now.AddSeconds(75)),now.AddSeconds(75));await blocker.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));Require(await controller.SuppressCurrentOutageAsync()==GuardOperationResult.Succeeded,"Countdown cancellation must succeed during final probe.");blocker.Complete(Offline(now.AddSeconds(75)));await final;Require(power.Count==0,"A stale final probe must never request shutdown after cancellation.");
}
static async Task VerifyOfflineHttpProbeAsync()
{
 var targets=new[]{new ConnectivityTarget("text",new Uri("https://offline.test/text"),HttpStatusCode.OK,"ok"),new ConnectivityTarget("status",new Uri("https://offline.test/status"),HttpStatusCode.NoContent,null)};
 var handler=new FakeHttpHandler(async(request,token)=>{await Task.Delay(40,token);return request.RequestUri!.AbsolutePath=="/text"?new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("ok")}:new HttpResponseMessage(HttpStatusCode.NoContent);});using(var probe=new ConnectivityProbeService(handler,targets,TimeSpan.FromSeconds(1))){var result=await probe.ProbeAsync();Require(result.IsOnline&&handler.MaxConcurrent==2,"Probe endpoints must start concurrently without live network access.");}
 using(var probe=new ConnectivityProbeService(new FakeHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect){Headers={Location=new Uri("https://offline.test/other")}})),[targets[0]],TimeSpan.FromSeconds(1))){Require(!(await probe.ProbeAsync()).IsOnline,"Redirects must not be accepted.");}
 using(var probe=new ConnectivityProbeService(new FakeHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("wrong")})),[targets[0]],TimeSpan.FromSeconds(1))){Require(!(await probe.ProbeAsync()).IsOnline,"Text probes must require an exact content match.");}
 using(var probe=new ConnectivityProbeService(new FakeHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))),[targets[1]],TimeSpan.FromSeconds(1))){Require(!(await probe.ProbeAsync()).IsOnline,"A 204 target must strictly require status 204.");}
 var large=new byte[1025];using(var probe=new ConnectivityProbeService(new FakeHttpHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(large)})),[targets[0]],TimeSpan.FromSeconds(1))){Require((await probe.ProbeAsync()).Endpoints[0].FailureCategory=="ResponseTooLarge","Bodies over 1024 bytes must be rejected.");}
 using(var probe=new ConnectivityProbeService(new FakeHttpHandler(async(_,token)=>{await Task.Delay(Timeout.InfiniteTimeSpan,token);return new HttpResponseMessage(HttpStatusCode.OK);}),targets,TimeSpan.FromMilliseconds(80))){var result=await probe.ProbeAsync();Require(result.Endpoints.All(x=>x.FailureCategory=="Timeout"),"Total probe budget must map endpoint cancellation to Timeout.");}
 using(var probe=new ConnectivityProbeService(new FakeHttpHandler(async(request,token)=>{if(request.RequestUri!.AbsolutePath=="/text")return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("ok")};await Task.Delay(Timeout.InfiniteTimeSpan,token);return new HttpResponseMessage(HttpStatusCode.NoContent);}),targets,TimeSpan.FromMilliseconds(80))){Require((await probe.ProbeAsync()).IsOnline,"One strict success must keep the aggregate online when the other endpoint times out.");}
 using(var probe=new ConnectivityProbeService(new FakeHttpHandler(async(_,token)=>{await Task.Delay(Timeout.InfiniteTimeSpan,token);return new HttpResponseMessage(HttpStatusCode.OK);}),targets,TimeSpan.FromSeconds(5))){using var cancelled=new CancellationTokenSource(20);try{await probe.ProbeAsync(cancelled.Token);throw new InvalidOperationException("Caller cancellation was swallowed.");}catch(OperationCanceledException)when(cancelled.IsCancellationRequested){}}
}
static async Task VerifySupervisorRecoveryAsync(string parent)
{
 var root=Path.Combine(parent,"supervisor");Directory.CreateDirectory(root);var probe=new ThrowOnceProbe();var warning=new FakeWarning();using var store=new PowerGuardSettingsStore(root);using var events=new PowerGuardEventStore(root);await using var controller=new PowerGuardController(probe,new FakePower(),store,events,warning,new PowerGuardSettings{StartupGraceSeconds=120},TimeSpan.FromMilliseconds(20));
 await controller.ActivateAsync();for(var i=0;i<100&&probe.Count<2;i++)await Task.Delay(10);Require(probe.Count>=2,"Monitor must continue probing after one internal fault.");Require(warning.CloseCount>0,"Monitoring fault must close the real warning surface.");var recent=await events.ReadRecentAsync();Require(recent.Any(x=>x.Type=="MonitoringFault")&&recent.Any(x=>x.Type=="MonitoringRecovered"),"Supervisor must record fault and fresh grace recovery.");await controller.DeactivateAsync();Require(controller.Snapshot.State==PowerGuardState.Disabled,"Deactivate must cleanly finish after monitor recovery.");
}
static async Task VerifyPackageAsync(string package)
{
 var root=Path.Combine(Path.GetTempPath(),$"PowerGuard-import-{Guid.NewGuid():N}");var modules=Path.Combine(root,"Modules");var data=Path.Combine(root,"Data");
 try
 {
  var reader=new ModuleManifestReader();var validator=new ModuleManifestValidator();var importer=new ModulePackageImporter(reader,validator);
  var id=await importer.ImportAsync(package,modules);Require(id=="qing.powerguard","Imported package id is incorrect.");
  var discovered=await new ModuleManifestScanner(reader,validator).ScanAsync(modules);Require(discovered.Count==1&&discovered[0].IsValid,"Latest host could not discover imported PowerGuard.");
  Directory.CreateDirectory(Path.Combine(data,id));await File.WriteAllTextAsync(Path.Combine(data,id,"settings.json"),"{\"GuardEnabled\":false}");
  await RunImportedLifecycleAsync(discovered,id,data);
  Console.WriteLine("Latest toolbox import/load/activate/deactivate/unload integration passed.");
 }
 finally
 {
  for(var i=0;i<3;i++){GC.Collect();GC.WaitForPendingFinalizers();await Task.Delay(50);}
  try{if(Directory.Exists(root))Directory.Delete(root,true);}catch(UnauthorizedAccessException){Console.WriteLine("Imported module files remain temporarily locked; OS temp cleanup will remove the isolated directory.");}
 }
}
static async Task RunImportedLifecycleAsync(IReadOnlyList<QingToolbox.Abstractions.Modules.DiscoveredModule> discovered,string id,string data)
{
 await using var runtime=new ModuleRuntimeManager(new InProcessModuleLoader(new FakeLocalization()));runtime.ReplaceDiscoveredModules(discovered);
 Require(runtime.GetRecord(id)?.State==QingToolbox.Abstractions.Modules.ModuleState.NotLoaded,"Import must remain NotLoaded.");
 await runtime.LoadAsync(id,data);Require(runtime.GetRecord(id)?.IsLoaded==true,"Latest host failed to load PowerGuard.");
 await runtime.ActivateAsync(id);await runtime.DeactivateAsync(id);await runtime.UnloadAsync(id);
}
sealed class FakeProbe:IConnectivityProbe{public Queue<ConnectivityProbeResult> Results{get;}=[];public Task<ConnectivityProbeResult> ProbeAsync(CancellationToken token=default)=>Task.FromResult(Results.Count>0?Results.Dequeue():new ConnectivityProbeResult(false,false,[new("fake",false,1,"Offline")],DateTimeOffset.UtcNow));}
sealed class BlockingProbe:IConnectivityProbe{public TaskCompletionSource Started{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);private readonly TaskCompletionSource<ConnectivityProbeResult> _result=new(TaskCreationOptions.RunContinuationsAsynchronously);public async Task<ConnectivityProbeResult> ProbeAsync(CancellationToken token=default){Started.TrySetResult();return await _result.Task.WaitAsync(token);}public void Complete(ConnectivityProbeResult result)=>_result.TrySetResult(result);}
sealed class ThrowOnceProbe:IConnectivityProbe{public int Count;public Task<ConnectivityProbeResult> ProbeAsync(CancellationToken token=default){if(Interlocked.Increment(ref Count)==1)throw new InvalidOperationException("safe synthetic monitor fault");return Task.FromResult(new ConnectivityProbeResult(true,true,[new("fake",true,1,"")],DateTimeOffset.UtcNow));}}
sealed class FakeHttpHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> response):HttpMessageHandler{private int _active,_max;public int MaxConcurrent=>_max;protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){var active=Interlocked.Increment(ref _active);int current;while(active>(current=_max))Interlocked.CompareExchange(ref _max,active,current);try{return await response(request,token);}finally{Interlocked.Decrement(ref _active);}}}
sealed class FakePower:IPowerActionService{public int Count;public PowerActionResult Result=PowerActionResult.Started;public Task<PowerActionResult> RequestNormalShutdownAsync(CancellationToken token=default){Count++;return Task.FromResult(Result);}}
sealed class RetryLauncher:IProcessLauncher{private int _count;public Process? Start(ProcessStartInfo info)=>Interlocked.Increment(ref _count)==1?null:Process.GetCurrentProcess();}
sealed class FakeWarning:IWarningPresenter{public int ShowCount,TestShowCount,CloseCount,UpdateCount;public bool Real;public Task ShowRealAsync(int seconds,CancellationToken token=default){Real=true;ShowCount++;return Task.CompletedTask;}public Task<TestPreviewResult> ShowTestAsync(int seconds,CancellationToken token=default){if(Real)return Task.FromResult(TestPreviewResult.UnavailableDuringCountdown);TestShowCount++;return Task.FromResult(TestPreviewResult.Opened);}public Task UpdateRealAsync(int seconds,CancellationToken token=default){UpdateCount++;return Task.CompletedTask;}public Task CloseRealAsync(CancellationToken token=default){Real=false;CloseCount++;return Task.CompletedTask;}public Task CloseTestAsync(CancellationToken token=default)=>Task.CompletedTask;public Task CloseAllAsync(CancellationToken token=default){Real=false;CloseCount++;return Task.CompletedTask;}}
sealed class FakeLocalization:ILocalizationService{public CultureInfo CurrentCulture=>CultureInfo.InvariantCulture;public string CurrentLanguageCode=>"en-US";public event EventHandler? CultureChanged { add { } remove { } } public string GetString(string key)=>key;public string GetString(string key,params object[] args)=>string.Format(CultureInfo.InvariantCulture,key,args);public string GetModuleString(string moduleId,string key,string? fallback=null)=>fallback??key;public string GetModuleString(string moduleId,string key,string? fallback,params object[] args)=>string.Format(CultureInfo.InvariantCulture,fallback??key,args);}
