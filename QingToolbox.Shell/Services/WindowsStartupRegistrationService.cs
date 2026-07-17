using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.Services;

public enum StartupRegistrationBackendKind { None, TaskScheduler, RegistryRun }
public enum StartupRegistrationHealth { Healthy, HealthyRegistryFallback, Disabled, DisabledExternally, Missing, ExecutableMoved, DefinitionChanged, SchedulerUnavailable, RegistryUnavailable, AccessDenied, MultipleRegistrations, UnknownFailure }
public sealed record StartupRegistrationState(
    bool ConfiguredByUser, StartupRegistrationBackendKind Backend, StartupRegistrationHealth Health,
    bool ExecutablePathMatches=false, bool ArgumentsMatch=false, bool WorkingDirectoryMatches=false,
    bool TriggerMatches=false, bool PrincipalMatches=false, bool SettingsMatch=false, bool IsEnabled=false,
    DateTimeOffset? LastRunTime=null, int? LastTaskResult=null, string DiagnosticCode="startup.unknown")
{
    public bool MatchesCurrentExecutable => Health is StartupRegistrationHealth.Healthy or StartupRegistrationHealth.HealthyRegistryFallback;
    public bool IsRegistered => Backend != StartupRegistrationBackendKind.None && Health != StartupRegistrationHealth.Missing;
}

public interface IStartupRegistrationBackend
{
    StartupRegistrationBackendKind Kind { get; }
    Task<StartupRegistrationState> GetStateAsync(CancellationToken token=default);
    Task<StartupRegistrationState> EnableAsync(CancellationToken token=default);
    Task DisableAsync(CancellationToken token=default);
    Task<StartupRegistrationState> RepairAsync(CancellationToken token=default);
    Task<StartupRegistrationState> RunTestAsync(CancellationToken token=default);
}

public interface IStartupRegistrationStore { string? Read(); void Write(string command); void Delete(); }
public sealed class WindowsRunRegistrationStore : IStartupRegistrationStore
{
    private const string KeyPath=@"Software\Microsoft\Windows\CurrentVersion\Run", ValueName="QingToolbox";
    public string? Read()=>Registry.CurrentUser.OpenSubKey(KeyPath)?.GetValue(ValueName) as string;
    public void Write(string command){using var key=Registry.CurrentUser.CreateSubKey(KeyPath,true);key.SetValue(ValueName,command,RegistryValueKind.String);}
    public void Delete(){using var key=Registry.CurrentUser.OpenSubKey(KeyPath,true);key?.DeleteValue(ValueName,false);}
}

public sealed class WindowsRunStartupBackend(IStartupRegistrationStore store, ApplicationExecutionEnvironment environment) : IStartupRegistrationBackend
{
    public StartupRegistrationBackendKind Kind=>StartupRegistrationBackendKind.RegistryRun;
    private static string Exe=>Path.GetFullPath(Environment.ProcessPath??throw new InvalidOperationException("Executable path unavailable."));
    public static string BuildCommand(string executablePath)=>$"\"{Path.GetFullPath(executablePath)}\" --startup --startup-source RegistryRun";
    public Task<StartupRegistrationState> GetStateAsync(CancellationToken token=default)=>Task.Run(()=>
    {
        if(!environment.AllowWindowsStartupRegistration)return State(StartupRegistrationHealth.RegistryUnavailable,"startup.environmentDisabled");
        try{var value=store.Read();if(value is null)return State(StartupRegistrationHealth.Missing,"startup.registryMissing");var matches=string.Equals(value,BuildCommand(Exe),StringComparison.OrdinalIgnoreCase);return new(true,Kind,matches?StartupRegistrationHealth.HealthyRegistryFallback:StartupRegistrationHealth.DefinitionChanged,matches,true,true,true,true,true,true,null,null,matches?"startup.registryFallback":"startup.registryChanged");}
        catch(UnauthorizedAccessException){return State(StartupRegistrationHealth.AccessDenied,"startup.registryAccessDenied");}catch{return State(StartupRegistrationHealth.RegistryUnavailable,"startup.registryUnavailable");}
    },token);
    public async Task<StartupRegistrationState> EnableAsync(CancellationToken token=default){if(BuildCommand(Exe).Length>260)throw new InvalidOperationException("Registry Run command exceeds 260 characters.");await Task.Run(()=>store.Write(BuildCommand(Exe)),token);return await GetStateAsync(token);}
    public Task DisableAsync(CancellationToken token=default)=>Task.Run(store.Delete,token);
    public Task<StartupRegistrationState> RepairAsync(CancellationToken token=default)=>EnableAsync(token);
    public Task<StartupRegistrationState> RunTestAsync(CancellationToken token=default)=>Task.FromResult(State(StartupRegistrationHealth.RegistryUnavailable,"startup.registryTestUnsupported"));
    private StartupRegistrationState State(StartupRegistrationHealth health,string code)=>new(false,Kind,health,DiagnosticCode:code);
}

public sealed record ScheduledStartupDefinition(string TaskPath,string ExecutablePath,string Arguments,string WorkingDirectory,string UserId,bool Enabled,bool LogonTrigger,bool InteractiveToken,bool LeastPrivilege,bool IgnoreNew,bool AllowOnBatteries,bool RunOnlyIfIdle,bool RunOnlyIfNetworkAvailable,bool WakeToRun,bool AllowStartOnDemand,string ExecutionTimeLimit,int RestartCount,string RestartInterval,DateTimeOffset? LastRunTime=null,int? LastTaskResult=null);
public interface ITaskSchedulerStore { ScheduledStartupDefinition? Read(); void Register(ScheduledStartupDefinition definition); void Delete(); void Run(); }

public sealed class WindowsTaskSchedulerStore : ITaskSchedulerStore
{
    public string TaskName { get; } = "Startup-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(WindowsIdentity.GetCurrent().User?.Value??Environment.UserName)))[..16];
    private string PreferredPath=>@"\QingToolbox\"+TaskName; private string FallbackPath=>@"\QingToolbox-"+TaskName;
    public ScheduledStartupDefinition? Read()
    {
        dynamic? service=null;
        try
        {
            service=Connect();dynamic? task=TryGet(service,PreferredPath)??TryGet(service,FallbackPath);if(task is null)return null;
            dynamic definition=task.Definition;
            dynamic? trigger=definition.Triggers.Count>0?definition.Triggers.Item(1):null;
            dynamic? action=definition.Actions.Count>0?definition.Actions.Item(1):null;
            string Text(dynamic? value)=>Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)??string.Empty;
            return new(Text(task.Path),Text(action?.Path),Text(action?.Arguments),Text(action?.WorkingDirectory),Text(definition.Principal.UserId),(bool)task.Enabled,
                trigger is not null&&(int)trigger.Type==9,(int)definition.Principal.LogonType==3,(int)definition.Principal.RunLevel==0,
                (int)definition.Settings.MultipleInstances==2,!(bool)definition.Settings.DisallowStartIfOnBatteries&&!(bool)definition.Settings.StopIfGoingOnBatteries,
                (bool)definition.Settings.RunOnlyIfIdle,(bool)definition.Settings.RunOnlyIfNetworkAvailable,(bool)definition.Settings.WakeToRun,
                (bool)definition.Settings.AllowDemandStart,Text(definition.Settings.ExecutionTimeLimit),(int)definition.Settings.RestartCount,
                Text(definition.Settings.RestartInterval),ToDate(task.LastRunTime),(int)task.LastTaskResult);
        }
        catch(COMException exception)when((uint)exception.HResult==0x80070002u){return null;}
        finally{Release(service);}
    }
    public void Register(ScheduledStartupDefinition value){dynamic? service=null;try{service=Connect();dynamic root=service.GetFolder("\\");dynamic folder;string name;try{try{folder=service.GetFolder("\\QingToolbox");}catch{folder=root.CreateFolder("QingToolbox");}name=TaskName;}catch{folder=root;name="QingToolbox-"+TaskName;}dynamic d=service.NewTask(0);d.RegistrationInfo.Description="Starts QingToolbox for the current user at sign-in.";d.Principal.UserId=value.UserId;d.Principal.LogonType=3;d.Principal.RunLevel=0;dynamic trigger=d.Triggers.Create(9);trigger.UserId=value.UserId;trigger.Enabled=true;dynamic action=d.Actions.Create(0);action.Path=value.ExecutablePath;action.Arguments=value.Arguments;action.WorkingDirectory=value.WorkingDirectory;d.Settings.Enabled=true;d.Settings.MultipleInstances=2;d.Settings.DisallowStartIfOnBatteries=false;d.Settings.StopIfGoingOnBatteries=false;d.Settings.RunOnlyIfIdle=false;d.Settings.RunOnlyIfNetworkAvailable=false;d.Settings.WakeToRun=false;d.Settings.AllowDemandStart=true;d.Settings.ExecutionTimeLimit="PT0S";d.Settings.RestartCount=3;d.Settings.RestartInterval="PT1M";folder.RegisterTaskDefinition(name,d,6,null,null,3,null);}finally{Release(service);}}
    public void Delete(){dynamic? service=null;try{service=Connect();foreach(var path in new[]{PreferredPath,FallbackPath})try{var split=path.LastIndexOf('\\');dynamic folder=service.GetFolder(split==0?"\\":path[..split]);folder.DeleteTask(path[(split+1)..],0);}catch(COMException exception)when((uint)exception.HResult is 0x80070002u or 0x80070003u){}}finally{Release(service);}}
    public void Run(){dynamic? service=null;try{service=Connect();dynamic task=TryGet(service,PreferredPath)??TryGet(service,FallbackPath)??throw new FileNotFoundException("Startup task is missing.");task.Run(null);}finally{Release(service);}}
    private static dynamic Connect(){var type=Type.GetTypeFromProgID("Schedule.Service")??throw new COMException("Task Scheduler COM is unavailable.");dynamic service=Activator.CreateInstance(type)!;service.Connect();return service;}
    private static dynamic? TryGet(dynamic service,string path){try{return service.GetFolder(path[..path.LastIndexOf('\\')]).GetTask(path[(path.LastIndexOf('\\')+1)..]);}catch(COMException exception)when((uint)exception.HResult is 0x80070002u or 0x80070003u){return null;}}
    private static DateTimeOffset? ToDate(object value)=>value is DateTime date&&date.Year>1900?new DateTimeOffset(date):null;
    private static void Release(object? value){if(value is not null&&Marshal.IsComObject(value))try{Marshal.FinalReleaseComObject(value);}catch{}}
}

public sealed class WindowsTaskSchedulerStartupBackend(ITaskSchedulerStore store,ApplicationExecutionEnvironment environment):IStartupRegistrationBackend
{
    public StartupRegistrationBackendKind Kind=>StartupRegistrationBackendKind.TaskScheduler;
    private static string Exe=>Path.GetFullPath(Environment.ProcessPath??throw new InvalidOperationException("Executable path unavailable."));
    private static string User=>WindowsIdentity.GetCurrent().Name;
    private static ScheduledStartupDefinition Desired=>new("",Exe,"--startup --startup-source TaskScheduler",Path.GetDirectoryName(Exe)!,User,true,true,true,true,true,true,false,false,false,true,"PT0S",3,"PT1M");
    public Task<StartupRegistrationState> GetStateAsync(CancellationToken token=default)=>Task.Run(()=>
    {
        if(!environment.AllowWindowsStartupRegistration)return State(StartupRegistrationHealth.SchedulerUnavailable,"startup.environmentDisabled");
        try
        {
            var actual=store.Read();if(actual is null)return State(StartupRegistrationHealth.Missing,"startup.taskMissing");
            var desired=Desired;
            var path=Eq(actual.ExecutablePath,desired.ExecutablePath);
            var args=actual.Arguments==desired.Arguments;
            var work=Eq(actual.WorkingDirectory,desired.WorkingDirectory);
            var trigger=actual.LogonTrigger;
            var principal=actual.InteractiveToken&&actual.LeastPrivilege&&string.Equals(actual.UserId,desired.UserId,StringComparison.OrdinalIgnoreCase);
            var settings=actual.IgnoreNew&&actual.AllowOnBatteries&&!actual.RunOnlyIfIdle&&!actual.RunOnlyIfNetworkAvailable&&!actual.WakeToRun&&actual.AllowStartOnDemand&&actual.ExecutionTimeLimit=="PT0S"&&actual.RestartCount==3&&actual.RestartInterval=="PT1M";
            var health=!actual.Enabled?StartupRegistrationHealth.DisabledExternally:path&&args&&work&&trigger&&principal&&settings?StartupRegistrationHealth.Healthy:!path?StartupRegistrationHealth.ExecutableMoved:StartupRegistrationHealth.DefinitionChanged;
            return new(true,Kind,health,path,args,work,trigger,principal,settings,actual.Enabled,actual.LastRunTime,actual.LastTaskResult,health switch{StartupRegistrationHealth.Healthy=>"startup.taskHealthy",StartupRegistrationHealth.DisabledExternally=>"startup.taskDisabledExternally",StartupRegistrationHealth.ExecutableMoved=>"startup.taskExecutableMoved",_=>"startup.taskChanged"});
        }
        catch(UnauthorizedAccessException){return State(StartupRegistrationHealth.AccessDenied,"startup.taskAccessDenied");}
        catch(COMException){return State(StartupRegistrationHealth.SchedulerUnavailable,"startup.schedulerUnavailable");}
        catch{return State(StartupRegistrationHealth.UnknownFailure,"startup.taskUnknown");}
    },token);
    public async Task<StartupRegistrationState> EnableAsync(CancellationToken token=default){var info=new FileInfo(Exe);if(!info.Exists||(info.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Executable must exist and cannot be a reparse point.");await Task.Run(()=>store.Register(Desired),token);return await GetStateAsync(token);}
    public Task DisableAsync(CancellationToken token=default)=>Task.Run(store.Delete,token);
    public Task<StartupRegistrationState> RepairAsync(CancellationToken token=default)=>EnableAsync(token);
    public async Task<StartupRegistrationState> RunTestAsync(CancellationToken token=default)
    {
        var before=store.Read();await Task.Run(store.Run,token);
        for(var attempt=0;attempt<25;attempt++)
        {
            await Task.Delay(200,token);var current=store.Read();
            if(current?.LastRunTime!=before?.LastRunTime||current?.LastTaskResult!=before?.LastTaskResult)return await GetStateAsync(token);
        }
        return await GetStateAsync(token);
    }
    private StartupRegistrationState State(StartupRegistrationHealth health,string code)=>new(false,Kind,health,DiagnosticCode:code);
    private static bool Eq(string a,string b)=>string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
}

public sealed class WindowsStartupRegistrationService(UserSettingsService settingsService,WindowsTaskSchedulerStartupBackend scheduler,WindowsRunStartupBackend registry,ApplicationExecutionEnvironment environment)
{
    public bool IsAvailable=>environment.AllowWindowsStartupRegistration;
    public async Task<StartupRegistrationState> GetStateAsync(CancellationToken token=default){if(!IsAvailable)return new(false,StartupRegistrationBackendKind.None,StartupRegistrationHealth.Disabled,DiagnosticCode:"startup.environmentDisabled");var settings=await settingsService.ReadAsync(token);var task=await scheduler.GetStateAsync(token);var run=await registry.GetStateAsync(token);if(task.MatchesCurrentExecutable&&run.MatchesCurrentExecutable)return task with{Health=StartupRegistrationHealth.MultipleRegistrations,DiagnosticCode="startup.multipleRegistrations"};if(settings.StartupRegistrationBackend=="RegistryRun"||run.MatchesCurrentExecutable)return run with{ConfiguredByUser=settings.LaunchAtLogin};return task with{ConfiguredByUser=settings.LaunchAtLogin};}
    public async Task SetEnabledAsync(bool enabled,CancellationToken token=default)
    {
        if(!IsAvailable)throw new InvalidOperationException("Startup registration is unavailable in this environment.");
        if(!enabled)
        {
            Exception? failure=null;
            try{await scheduler.DisableAsync(token);}catch(Exception exception){failure=exception;}
            try{await registry.DisableAsync(token);}catch(Exception exception){failure??=exception;}
            var task=await scheduler.GetStateAsync(token);var run=await registry.GetStateAsync(token);
            if(failure is not null||task.IsRegistered||run.IsRegistered)throw new IOException("One or more startup registrations could not be removed.",failure);
            await settingsService.UpdateAsync(s=>{s.LaunchAtLogin=false;s.StartupRegistrationBackend="None";},token);return;
        }

        StartupRegistrationState state;
        try{state=await scheduler.EnableAsync(token);}
        catch(Exception exception)when(exception is COMException or UnauthorizedAccessException or IOException)
        {
            state=await registry.EnableAsync(token);if(state.Health!=StartupRegistrationHealth.HealthyRegistryFallback)throw;
            await settingsService.UpdateAsync(s=>{s.LaunchAtLogin=true;s.StartupRegistrationBackend="RegistryRun";},token);return;
        }
        if(state.Health!=StartupRegistrationHealth.Healthy)
        {
            await scheduler.DisableAsync(token);
            throw new IOException("Task definition verification failed.");
        }
        try{await registry.DisableAsync(token);}
        catch{try{await scheduler.DisableAsync(token);}catch{}throw;}
        await settingsService.UpdateAsync(s=>{s.LaunchAtLogin=true;s.StartupRegistrationBackend="TaskScheduler";},token);
    }
    public async Task<StartupRegistrationState> RepairAsync(CancellationToken token=default){var settings=await settingsService.ReadAsync(token);var state=settings.StartupRegistrationBackend=="RegistryRun"?await registry.RepairAsync(token):await scheduler.RepairAsync(token);if(state.MatchesCurrentExecutable)await settingsService.UpdateAsync(s=>{s.LaunchAtLogin=true;s.StartupRegistrationBackend=state.Backend.ToString();},token);return state;}
    public async Task<StartupRegistrationState> RunTestAsync(CancellationToken token=default){var state=await GetStateAsync(token);return state.Backend==StartupRegistrationBackendKind.TaskScheduler?await scheduler.RunTestAsync(token):await registry.RunTestAsync(token);}
    public Task ReconcileAsync(UserSettings settings)=>Task.CompletedTask;
}
