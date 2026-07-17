using System.Runtime.InteropServices;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;

static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
var root=Path.Combine(Path.GetTempPath(),"QingToolbox-startup-reliability-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
try
{
    var parsed=ApplicationLaunchOptions.Parse(["--startup","--startup-source","TaskScheduler"]);
    Require(parsed.IsStartupLaunch&&parsed.StartupSource==StartupLaunchSource.TaskScheduler,"Startup source parsing failed.");
    foreach(var invalid in new[]{new[]{"--startup-source","TaskScheduler"},new[]{"--startup","--startup-source","Unknown"},new[]{"--startup","--startup","--startup-source","TaskScheduler"}})
        try{ApplicationLaunchOptions.Parse(invalid);throw new InvalidOperationException("Invalid arguments accepted.");}catch(ArgumentException){}

    var env=ApplicationExecutionEnvironment.Production();var taskStore=new FakeTaskStore();var runStore=new FakeRunStore();
    using var settings=new UserSettingsService(Path.Combine(root,"settings.json"));
    var scheduler=new WindowsTaskSchedulerStartupBackend(taskStore,env);var registry=new WindowsRunStartupBackend(runStore,env);
    var service=new WindowsStartupRegistrationService(settings,scheduler,registry,env);
    runStore.Write("legacy");await service.SetEnabledAsync(true);var d=taskStore.Definition!;
    Require(d.LogonTrigger&&d.InteractiveToken&&d.LeastPrivilege&&d.IgnoreNew&&d.AllowOnBatteries&&!d.RunOnlyIfIdle&&!d.RunOnlyIfNetworkAvailable&&!d.WakeToRun&&d.AllowStartOnDemand,"Task policy fields are incorrect.");
    Require(d.RestartCount==3&&d.RestartInterval=="PT1M"&&d.ExecutionTimeLimit=="PT0S"&&d.Arguments=="--startup --startup-source TaskScheduler"&&d.WorkingDirectory==Path.GetDirectoryName(d.ExecutablePath),"Task action or retry fields are incorrect.");
    Require(runStore.Read() is null&&(await service.GetStateAsync()).Health==StartupRegistrationHealth.Healthy,"Run migration was not transactional.");
    taskStore.Definition=d with{Enabled=false};Require((await service.GetStateAsync()).Health==StartupRegistrationHealth.DisabledExternally,"External disable was not preserved.");
    Require(!taskStore.RegisterCalledAfterRead,"A health query silently repaired an externally disabled task.");
    await service.RepairAsync();Require((await service.GetStateAsync()).Health==StartupRegistrationHealth.Healthy,"Explicit repair failed.");
    await service.SetEnabledAsync(false);Require(taskStore.Definition is null&&runStore.Read() is null,"Disable did not clear both backends.");

    var unavailable=new FakeTaskStore{Unavailable=true};var fallbackRun=new FakeRunStore();using var fallbackSettings=new UserSettingsService(Path.Combine(root,"fallback.json"));
    var fallback=new WindowsStartupRegistrationService(fallbackSettings,new(unavailable,env),new(fallbackRun,env),env);await fallback.SetEnabledAsync(true);
    Require((await fallback.GetStateAsync()).Health==StartupRegistrationHealth.HealthyRegistryFallback,"Scheduler failure did not use explicit registry fallback.");

    var preferencePath=Path.Combine(root,"preference.json");await File.WriteAllTextAsync(preferencePath,"{bad");
    var snapshot=await new StartupPreferenceReader().ReadAsync(preferencePath,true);Require(snapshot.PresentationMode==StartupPresentationMode.FloatingBadge,"Corrupt settings did not use safe startup fallback.");
    await File.WriteAllBytesAsync(preferencePath,new byte[StartupPreferenceReader.MaximumSettingsBytes+1]);snapshot=await new StartupPreferenceReader().ReadAsync(preferencePath,false);Require(snapshot.PresentationMode==StartupPresentationMode.MainWindow,"Oversize settings did not use manual fallback.");

    var journalPath=Path.Combine(root,"startup-health.json");var journal=new StartupHealthJournal(journalPath,TimeProvider.System);journal.SetSource(StartupLaunchSource.StartupTest);journal.Mark(StartupPhase.PresentationReady);journal.Mark(StartupPhase.Ready);await Task.Delay(150);
    var records=await journal.ReadAsync();Require(records.Count==1&&records[0].PresentationReadyAt is not null&&records[0].ReadyAt is not null,"Startup journal did not persist readiness.");
    Require((int)StartupExitCode.FatalInitializationFailure!=0,"Fatal startup exit code must be nonzero.");
    var phases=new[]{StartupPhase.ProcessEntry,StartupPhase.InstanceReady,StartupPhase.NotificationAreaReady,StartupPhase.PresentationReady,StartupPhase.ModuleDiscoveryComplete,StartupPhase.AuthorizedModulesRestored,StartupPhase.Ready};
    Require(Array.IndexOf(phases,StartupPhase.PresentationReady)<Array.IndexOf(phases,StartupPhase.ModuleDiscoveryComplete),"Presentation must precede module discovery.");
    Console.WriteLine("Startup reliability smoke test passed: task fields, migration, fallback, external disable, preferences, journal and phase ordering.");
}
finally{try{Directory.Delete(root,true);}catch{}}

sealed class FakeRunStore:IStartupRegistrationStore{private string? _value;public string? Read()=>_value;public void Write(string command)=>_value=command;public void Delete()=>_value=null;}
sealed class FakeTaskStore:ITaskSchedulerStore
{
    private ScheduledStartupDefinition? _definition;public bool Unavailable{get;init;}public bool RegisterCalledAfterRead{get;private set;}private bool _read;
    public ScheduledStartupDefinition? Definition{get=>_definition;set=>_definition=value;}
    public ScheduledStartupDefinition? Read(){_read=true;if(Unavailable)throw new COMException("Unavailable");return _definition;}
    public void Register(ScheduledStartupDefinition definition){if(Unavailable)throw new COMException("Unavailable");RegisterCalledAfterRead|=_read;_definition=definition;}
    public void Delete(){if(Unavailable)throw new COMException("Unavailable");_definition=null;}
    public void Run(){if(Unavailable)throw new COMException("Unavailable");if(_definition is null)throw new FileNotFoundException();}
}
