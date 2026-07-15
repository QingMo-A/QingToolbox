using QingToolbox.Modules.PowerGuard.Models;
using QingToolbox.Modules.PowerGuard.Services;
using QingToolbox.Modules.PowerGuard.State;
using QingToolbox.ModuleLoader;
using QingToolbox.Core.Runtime;
using QingToolbox.Shell.Services;
using QingToolbox.Abstractions.Localization;
using System.Globalization;
using System.Diagnostics;

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
 await controller.ExtendCountdownAsync();Require(controller.Snapshot.RemainingSeconds>600,"Extension must add ten minutes.");
 await controller.SuppressCurrentOutageAsync();Require(controller.Snapshot.State==PowerGuardState.SuppressedForCurrentOutage&&warning.CloseCount>0,"Cancel must suppress and close warning.");
 var persisted=await store.ReadAsync();Require(persisted.SuppressedUntilConnectivityRestored,"Suppression must persist.");
 await controller.ProcessProbeAsync(Offline(start.AddMinutes(20)),start.AddMinutes(20));Require(warning.ShowCount==1&&power.Count==0,"Suppressed outage must not warn or shut down.");
 await controller.ProcessProbeAsync(Online(start.AddMinutes(21)),start.AddMinutes(21));await controller.ProcessProbeAsync(Online(start.AddMinutes(21).AddSeconds(30)),start.AddMinutes(21).AddSeconds(30));Require(!controller.Settings.SuppressedUntilConnectivityRestored&&controller.Snapshot.State==PowerGuardState.Online,"Stable recovery must clear suppression and rearm.");
 await controller.ProcessProbeAsync(Offline(start.AddMinutes(22)),start.AddMinutes(22));await controller.ProcessProbeAsync(Offline(start.AddMinutes(23)),start.AddMinutes(23));
 probe.Results.Enqueue(Offline(start.AddMinutes(33)));await controller.ProcessProbeAsync(Offline(start.AddMinutes(33)),start.AddMinutes(33));Require(power.Count==1&&controller.Snapshot.State==PowerGuardState.ExecutingShutdown,"Expired offline countdown must request one normal shutdown after a final offline probe.");
 await controller.ProcessProbeAsync(Offline(start.AddMinutes(34)),start.AddMinutes(34));Require(power.Count==1,"Power action must not repeat.");
 controller.ResumeForTesting(start.AddMinutes(22));Require(controller.Snapshot.State==PowerGuardState.StartupGrace,"Resume must restart grace.");
 var beforeTestPower=power.Count;await controller.ShowTestWarningAsync();Require(power.Count==beforeTestPower&&warning.TestShowCount==1,"Test warning must never invoke power action.");

 var machine=new PowerGuardStateMachine();Require(!machine.TryMoveTo(PowerGuardState.ExecutingShutdown,DateTimeOffset.UtcNow,out _),"Illegal state transitions must be rejected.");Require(machine.State==PowerGuardState.Disabled,"Rejected transition must not mutate state.");
 var launcher=new RetryLauncher();var powerService=new PowerActionService(launcher);Require(await powerService.RequestNormalShutdownAsync()==PowerActionResult.Failed,"A failed launch must be reported.");Require(await powerService.RequestNormalShutdownAsync()==PowerActionResult.Started,"A failed launch must remain retriable.");Require(await powerService.RequestNormalShutdownAsync()==PowerActionResult.AlreadyRequested,"A successful request must be latched.");
 await Task.WhenAll(store.UpdateAsync(s=>s.SuppressedUntilConnectivityRestored=true),store.UpdateAsync(s=>s.ShowRecoveryNotification=false));var merged=await store.ReadAsync();Require(merged.SuppressedUntilConnectivityRestored&&!merged.ShowRecoveryNotification,"Concurrent partial settings updates must not overwrite one another.");

 await File.WriteAllTextAsync(Path.Combine(root,"settings.json"),"{ invalid");var safe=await store.ReadAsync();Require(safe.GuardEnabled&&Directory.GetFiles(root,"settings.corrupt-*.json").Length==1,"Corrupt settings must use defaults and be backed up.");
 Console.WriteLine("PowerGuard smoke test passed: state, suppression, recovery, extension, resume, safe settings and no real power action.");
 store.Dispose();events.Dispose();
}
finally{if(Directory.Exists(root))Directory.Delete(root,true);}

if(args.Length==2&&args[0]=="--package") await VerifyPackageAsync(args[1]);

static ConnectivityProbeResult Online(DateTimeOffset now)=>new(true,true,[new("fake",true,1,"")],now);
static ConnectivityProbeResult Offline(DateTimeOffset now)=>new(false,false,[new("fake",false,1,"Offline")],now);
static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
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
sealed class FakePower:IPowerActionService{public int Count;public PowerActionResult Result=PowerActionResult.Started;public Task<PowerActionResult> RequestNormalShutdownAsync(CancellationToken token=default){Count++;return Task.FromResult(Result);}}
sealed class RetryLauncher:IProcessLauncher{private int _count;public Process? Start(ProcessStartInfo info)=>Interlocked.Increment(ref _count)==1?null:Process.GetCurrentProcess();}
sealed class FakeWarning:IWarningPresenter{public int ShowCount,TestShowCount,CloseCount,UpdateCount;public bool Real;public Task ShowRealAsync(int seconds,CancellationToken token=default){Real=true;ShowCount++;return Task.CompletedTask;}public Task<TestPreviewResult> ShowTestAsync(int seconds,CancellationToken token=default){if(Real)return Task.FromResult(TestPreviewResult.UnavailableDuringCountdown);TestShowCount++;return Task.FromResult(TestPreviewResult.Opened);}public Task UpdateRealAsync(int seconds,CancellationToken token=default){UpdateCount++;return Task.CompletedTask;}public Task CloseRealAsync(CancellationToken token=default){Real=false;CloseCount++;return Task.CompletedTask;}public Task CloseTestAsync(CancellationToken token=default)=>Task.CompletedTask;public Task CloseAllAsync(CancellationToken token=default){Real=false;CloseCount++;return Task.CompletedTask;}}
sealed class FakeLocalization:ILocalizationService{public CultureInfo CurrentCulture=>CultureInfo.InvariantCulture;public string CurrentLanguageCode=>"en-US";public event EventHandler? CultureChanged { add { } remove { } } public string GetString(string key)=>key;public string GetString(string key,params object[] args)=>string.Format(CultureInfo.InvariantCulture,key,args);public string GetModuleString(string moduleId,string key,string? fallback=null)=>fallback??key;public string GetModuleString(string moduleId,string key,string? fallback,params object[] args)=>string.Format(CultureInfo.InvariantCulture,fallback??key,args);}
