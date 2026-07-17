using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;
using QingToolbox.Core.Settings;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.Services;

public enum StartupRegistrationBackendKind { None, TaskScheduler, RegistryRun }
public enum StartupRegistrationPresence { Absent, Present, Unknown }
public enum StartupRegistrationHealth
{
    Healthy, HealthyRegistryFallback, Disabled, DisabledExternally, Missing,
    ExecutableMoved, DefinitionChanged, ConfigurationDrift, SchedulerUnavailable,
    RegistryUnavailable, RegistryFallbackWithTaskStateUnknown, AccessDenied,
    MultipleRegistrations, MultipleTaskSchedulerRegistrations, PartialFailure, UnknownFailure
}

public sealed record StartupRegistrationState(
    bool ConfiguredByUser,
    StartupRegistrationBackendKind Backend,
    StartupRegistrationHealth Health,
    bool ExecutablePathMatches = false,
    bool ArgumentsMatch = false,
    bool WorkingDirectoryMatches = false,
    bool TriggerMatches = false,
    bool PrincipalMatches = false,
    bool SettingsMatch = false,
    bool IsEnabled = false,
    DateTimeOffset? LastRunTime = null,
    int? LastTaskResult = null,
    string DiagnosticCode = "startup.unknown",
    StartupRegistrationPresence Presence = StartupRegistrationPresence.Unknown)
{
    public bool MatchesCurrentExecutable =>
        Health is StartupRegistrationHealth.Healthy or StartupRegistrationHealth.HealthyRegistryFallback;

    public bool IsRegistered => Presence == StartupRegistrationPresence.Present;

    public static StartupRegistrationPresence PresenceFor(StartupRegistrationHealth health) => health switch
    {
        StartupRegistrationHealth.Missing or StartupRegistrationHealth.Disabled => StartupRegistrationPresence.Absent,
        StartupRegistrationHealth.SchedulerUnavailable or StartupRegistrationHealth.RegistryUnavailable or
            StartupRegistrationHealth.AccessDenied or StartupRegistrationHealth.UnknownFailure => StartupRegistrationPresence.Unknown,
        _ => StartupRegistrationPresence.Present
    };
}

public sealed record OwnedStartupTaskIdentity(
    string PreferredFolderPath,
    string PreferredTaskName,
    string PreferredTaskPath,
    string FallbackFolderPath,
    string FallbackTaskName,
    string FallbackTaskPath,
    string CurrentUserSid)
{
    public static OwnedStartupTaskIdentity Create(string? sid = null)
    {
        sid = string.IsNullOrWhiteSpace(sid)
            ? WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current user SID is unavailable.")
            : sid.Trim();
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sid)))[..16];
        var taskName = $"Startup-{hash}";
        return new("\\QingToolbox", taskName, $"\\QingToolbox\\{taskName}", "\\",
            $"QingToolbox-{taskName}", $"\\QingToolbox-{taskName}", sid);
    }

    public IEnumerable<string> OwnedPaths => [PreferredTaskPath, FallbackTaskPath];
    public string PreferredTestPath(Guid testId) => $"{PreferredFolderPath}\\Test-{testId:D}";
    public string FallbackTestPath(Guid testId) => $"\\QingToolbox-Test-{testId:D}";

    public bool IsOwnedTestPath(string taskPath)
    {
        try
        {
            var (folder, name) = SplitTaskPath(taskPath);
            var prefix = folder.Equals(PreferredFolderPath, StringComparison.OrdinalIgnoreCase)
                ? "Test-" : folder == "\\" ? "QingToolbox-Test-" : string.Empty;
            return prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal) &&
                Guid.TryParseExact(name[prefix.Length..], "D", out _);
        }
        catch (ArgumentException) { return false; }
    }

    public static (string Folder, string Name) SplitTaskPath(string taskPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskPath);
        if (!taskPath.StartsWith('\\') || taskPath.EndsWith('\\') ||
            taskPath.Any(char.IsControl))
            throw new ArgumentException("Task path must be an absolute, non-empty Task Scheduler path.", nameof(taskPath));
        var separator = taskPath.LastIndexOf('\\');
        var folder = separator == 0 ? "\\" : taskPath[..separator];
        var name = taskPath[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.Contains('/') || folder.Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new ArgumentException("Task path contains an invalid folder or task name.", nameof(taskPath));
        return (folder, name);
    }
}

public interface IStartupRegistrationBackend
{
    StartupRegistrationBackendKind Kind { get; }
    Task<StartupRegistrationState> GetStateAsync(CancellationToken token = default);
    Task<StartupRegistrationState> EnableAsync(CancellationToken token = default);
    Task DisableAsync(CancellationToken token = default);
    Task<StartupRegistrationState> RepairAsync(CancellationToken token = default);
    Task<StartupRegistrationState> RunTestAsync(CancellationToken token = default);
}

public interface IStartupTestCoordinator
{
    Task<StartupRegistrationState> RunAsync(CancellationToken token = default);
}

public interface IStartupRegistrationStore { string? Read(); void Write(string command); void Delete(); }

public sealed class WindowsRunRegistrationStore : IStartupRegistrationStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QingToolbox";
    public string? Read() => Registry.CurrentUser.OpenSubKey(KeyPath)?.GetValue(ValueName) as string;
    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

public sealed class WindowsRunStartupBackend(
    IStartupRegistrationStore store,
    ApplicationExecutionEnvironment environment) : IStartupRegistrationBackend
{
    public StartupRegistrationBackendKind Kind => StartupRegistrationBackendKind.RegistryRun;
    private static string Exe => Path.GetFullPath(Environment.ProcessPath ??
        throw new InvalidOperationException("Executable path unavailable."));
    public static string BuildCommand(string executablePath) =>
        $"\"{Path.GetFullPath(executablePath)}\" --startup --startup-source RegistryRun";

    public Task<StartupRegistrationState> GetStateAsync(CancellationToken token = default) => Task.Run(() =>
    {
        if (!environment.AllowWindowsStartupRegistration)
            return State(StartupRegistrationHealth.RegistryUnavailable, "startup.environmentDisabled");
        try
        {
            var value = store.Read();
            if (value is null) return State(StartupRegistrationHealth.Missing, "startup.registryMissing");
            var matches = string.Equals(value, BuildCommand(Exe), StringComparison.OrdinalIgnoreCase);
            return CreateState(true, matches ? StartupRegistrationHealth.HealthyRegistryFallback : StartupRegistrationHealth.DefinitionChanged,
                matches, matches ? "startup.registryFallback" : "startup.registryChanged");
        }
        catch (UnauthorizedAccessException) { return State(StartupRegistrationHealth.AccessDenied, "startup.registryAccessDenied"); }
        catch (Exception exception) when (exception is IOException or System.Security.SecurityException)
        { return State(StartupRegistrationHealth.RegistryUnavailable, "startup.registryUnavailable"); }
    }, token);

    public async Task<StartupRegistrationState> EnableAsync(CancellationToken token = default)
    {
        if (BuildCommand(Exe).Length > 260) throw new InvalidOperationException("Registry Run command exceeds 260 characters.");
        await Task.Run(() => store.Write(BuildCommand(Exe)), token);
        return await GetStateAsync(token);
    }
    public Task DisableAsync(CancellationToken token = default) => Task.Run(store.Delete, token);
    public Task<StartupRegistrationState> RepairAsync(CancellationToken token = default) => EnableAsync(token);
    public Task<StartupRegistrationState> RunTestAsync(CancellationToken token = default) =>
        Task.FromResult(State(StartupRegistrationHealth.RegistryUnavailable, "startup.registryTestUnsupported"));
    public string? Capture() => store.Read();
    public void Restore(string? command)
    {
        if (command is null) store.Delete(); else store.Write(command);
    }
    private StartupRegistrationState State(StartupRegistrationHealth health, string code) =>
        CreateState(false, health, false, code);
    private StartupRegistrationState CreateState(bool configured, StartupRegistrationHealth health, bool matches, string code) =>
        new(configured, Kind, health, matches, matches, matches, matches, matches, matches, matches,
            DiagnosticCode: code, Presence: StartupRegistrationState.PresenceFor(health));
}

public sealed record ScheduledStartupDefinition(
    string TaskPath, string ExecutablePath, string Arguments, string WorkingDirectory, string UserId,
    bool Enabled, bool LogonTrigger, bool InteractiveToken, bool LeastPrivilege, bool IgnoreNew,
    bool AllowOnBatteries, bool RunOnlyIfIdle, bool RunOnlyIfNetworkAvailable, bool WakeToRun,
    bool AllowStartOnDemand, string ExecutionTimeLimit, int RestartCount, string RestartInterval,
    DateTimeOffset? LastRunTime = null, int? LastTaskResult = null,
    string TaskName = "", int TriggerCount = 1, int ActionCount = 1, int ActionType = 0,
    bool TriggerEnabled = true, string TriggerUserId = "", bool TaskSettingsEnabled = true);

public enum OwnedTaskSchedulerState { Available, Unavailable, PartialFailure }

public sealed record OwnedStartupTaskSnapshot(
    ScheduledStartupDefinition? PreferredDefinition,
    ScheduledStartupDefinition? FallbackDefinition,
    OwnedTaskSchedulerState SchedulerState,
    IReadOnlyList<string>? FailedPaths = null)
{
    public bool HasBoth => PreferredDefinition is not null && FallbackDefinition is not null;
    public bool HasAny => PreferredDefinition is not null || FallbackDefinition is not null;
    public IReadOnlyList<string> ReadFailures => FailedPaths ?? Array.Empty<string>();
}

public interface ITaskSchedulerStore
{
    OwnedStartupTaskSnapshot CaptureOwned();
    void Register(ScheduledStartupDefinition definition);
    void RegisterAtPath(string taskPath, ScheduledStartupDefinition definition);
    void Delete();
    void DeleteAtPath(string taskPath);
    void RunAtPath(string taskPath);
    void DeleteOwnedStartupTests();
}

public sealed class WindowsTaskSchedulerStore : ITaskSchedulerStore
{
    public WindowsTaskSchedulerStore() : this(OwnedStartupTaskIdentity.Create()) { }
    public WindowsTaskSchedulerStore(OwnedStartupTaskIdentity identity) => Identity = identity;
    public OwnedStartupTaskIdentity Identity { get; }

    public OwnedStartupTaskSnapshot CaptureOwned()
    {
        object? service = null;
        try
        {
            service = Connect();
            ScheduledStartupDefinition? preferred = null, fallback = null;
            var failures = new List<string>();
            foreach (var path in Identity.OwnedPaths)
            {
                object? task = null;
                try
                {
                    task = TryGet(service, path);
                    if (task is null) continue;
                    var definition = ReadDefinition(task);
                    if (string.Equals(path, Identity.PreferredTaskPath, StringComparison.OrdinalIgnoreCase))
                        preferred = definition;
                    else
                        fallback = definition;
                }
                catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
                { failures.Add(path); }
                finally { Release(task); }
            }
            return new(preferred, fallback,
                failures.Count == 0 ? OwnedTaskSchedulerState.Available : OwnedTaskSchedulerState.PartialFailure,
                failures);
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException)
        { return new(null, null, OwnedTaskSchedulerState.Unavailable, Identity.OwnedPaths.ToArray()); }
        finally { Release(service); }
    }

    public void Register(ScheduledStartupDefinition value)
    {
        object? service = null; object? root = null; object? folder = null; object? definition = null;
        object? registrationInfo = null; object? principal = null; object? triggers = null; object? trigger = null;
        object? actions = null; object? action = null; object? settings = null; object? registeredTask = null;
        var useFallback = false;
        try
        {
            service = Connect();
            root = Invoke(service, "GetFolder", "\\");
            try
            {
                try { folder = Invoke(service, "GetFolder", Identity.PreferredFolderPath); }
                catch (COMException exception) when (IsMissing(exception))
                { folder = Invoke(root, "CreateFolder", Identity.PreferredFolderPath.TrimStart('\\')); }
            }
            catch (COMException exception) when (IsFolderRegistrationFailure(exception))
            { folder = root; root = null; useFallback = true; }

            definition = Invoke(service, "NewTask", 0);
            registrationInfo = Get(definition, "RegistrationInfo"); Set(registrationInfo, "Description", "Starts QingToolbox for the current user at sign-in.");
            principal = Get(definition, "Principal"); Set(principal, "UserId", value.UserId); Set(principal, "LogonType", 3); Set(principal, "RunLevel", 0);
            triggers = Get(definition, "Triggers"); trigger = Invoke(triggers, "Create", 9); Set(trigger, "UserId", value.UserId); Set(trigger, "Enabled", true);
            actions = Get(definition, "Actions"); action = Invoke(actions, "Create", 0); Set(action, "Path", value.ExecutablePath); Set(action, "Arguments", value.Arguments); Set(action, "WorkingDirectory", value.WorkingDirectory);
            settings = Get(definition, "Settings");
            Set(settings, "Enabled", true); Set(settings, "MultipleInstances", 2);
            Set(settings, "DisallowStartIfOnBatteries", false); Set(settings, "StopIfGoingOnBatteries", false);
            Set(settings, "RunOnlyIfIdle", false); Set(settings, "RunOnlyIfNetworkAvailable", false);
            Set(settings, "WakeToRun", false); Set(settings, "AllowDemandStart", true);
            Set(settings, "ExecutionTimeLimit", "PT0S"); Set(settings, "RestartCount", 3); Set(settings, "RestartInterval", "PT1M");
            var taskName = useFallback ? Identity.FallbackTaskName : Identity.PreferredTaskName;
            registeredTask = Invoke(folder, "RegisterTaskDefinition", taskName, definition, 6, null!, null!, 3, null!);
            Set(registeredTask, "Enabled", value.Enabled);
        }
        finally
        {
            Release(registeredTask); Release(settings); Release(action); Release(actions); Release(trigger); Release(triggers);
            Release(principal); Release(registrationInfo); Release(definition); Release(folder); Release(root); Release(service);
        }
    }

    public void RegisterAtPath(string taskPath, ScheduledStartupDefinition value)
    {
        if (!Identity.OwnedPaths.Contains(taskPath, StringComparer.OrdinalIgnoreCase) && !Identity.IsOwnedTestPath(taskPath))
            throw new ArgumentException("Only exact owned startup task paths may be registered.", nameof(taskPath));
        RegisterExact(taskPath, value);
    }

    private static void RegisterExact(string taskPath, ScheduledStartupDefinition value)
    {
        object? service = null; object? root = null; object? folder = null; object? definition = null;
        object? registrationInfo = null; object? principal = null; object? triggers = null; object? trigger = null;
        object? actions = null; object? action = null; object? settings = null; object? registeredTask = null;
        try
        {
            service = Connect();
            var (folderPath, taskName) = OwnedStartupTaskIdentity.SplitTaskPath(taskPath);
            root = Invoke(service, "GetFolder", "\\");
            if (folderPath == "\\") { folder = root; root = null; }
            else
            {
                try { folder = Invoke(service, "GetFolder", folderPath); }
                catch (COMException exception) when (IsMissing(exception))
                { folder = Invoke(root, "CreateFolder", folderPath.TrimStart('\\')); }
            }
            definition = Invoke(service, "NewTask", 0);
            registrationInfo = Get(definition, "RegistrationInfo"); Set(registrationInfo, "Description", "Starts QingToolbox for the current user at sign-in.");
            principal = Get(definition, "Principal"); Set(principal, "UserId", value.UserId); Set(principal, "LogonType", 3); Set(principal, "RunLevel", 0);
            triggers = Get(definition, "Triggers");
            if (value.LogonTrigger) { trigger = Invoke(triggers, "Create", 9); Set(trigger, "UserId", value.TriggerUserId); Set(trigger, "Enabled", value.TriggerEnabled); }
            actions = Get(definition, "Actions"); action = Invoke(actions, "Create", 0); Set(action, "Path", value.ExecutablePath); Set(action, "Arguments", value.Arguments); Set(action, "WorkingDirectory", value.WorkingDirectory);
            settings = Get(definition, "Settings"); Set(settings, "Enabled", value.TaskSettingsEnabled); Set(settings, "MultipleInstances", 2);
            Set(settings, "DisallowStartIfOnBatteries", !value.AllowOnBatteries); Set(settings, "StopIfGoingOnBatteries", !value.AllowOnBatteries);
            Set(settings, "RunOnlyIfIdle", value.RunOnlyIfIdle); Set(settings, "RunOnlyIfNetworkAvailable", value.RunOnlyIfNetworkAvailable);
            Set(settings, "WakeToRun", value.WakeToRun); Set(settings, "AllowDemandStart", value.AllowStartOnDemand);
            Set(settings, "ExecutionTimeLimit", value.ExecutionTimeLimit); Set(settings, "RestartCount", value.RestartCount); Set(settings, "RestartInterval", value.RestartInterval);
            registeredTask = Invoke(folder, "RegisterTaskDefinition", taskName, definition, 6, null!, null!, 3, null!);
        }
        finally
        {
            Release(registeredTask); Release(settings); Release(action); Release(actions); Release(trigger); Release(triggers);
            Release(principal); Release(registrationInfo); Release(definition); Release(folder); Release(root); Release(service);
        }
    }

    public void Delete()
    {
        object? service = null;
        try
        {
            service = Connect();
            foreach (var path in Identity.OwnedPaths)
            {
                var (folderPath, name) = OwnedStartupTaskIdentity.SplitTaskPath(path);
                object? folder = null;
                try { folder = Invoke(service, "GetFolder", folderPath); _ = Invoke(folder, "DeleteTask", name, 0); }
                catch (COMException exception) when (IsMissing(exception)) { }
                finally { Release(folder); }
            }
            TryDeletePreferredFolder(service);
        }
        finally { Release(service); }
    }

    public void DeleteAtPath(string taskPath)
    {
        if (!Identity.OwnedPaths.Contains(taskPath, StringComparer.OrdinalIgnoreCase) && !Identity.IsOwnedTestPath(taskPath))
            throw new ArgumentException("Only exact owned startup task paths may be deleted.", nameof(taskPath));
        object? service = null; object? folder = null;
        try
        {
            service = Connect();
            var (folderPath, name) = OwnedStartupTaskIdentity.SplitTaskPath(taskPath);
            try { folder = Invoke(service, "GetFolder", folderPath); _ = Invoke(folder, "DeleteTask", name, 0); }
            catch (COMException exception) when (IsMissing(exception)) { }
        }
        finally { Release(folder); Release(service); }
    }

    public void RunAtPath(string taskPath)
    {
        object? service = null; object? task = null; object? running = null;
        try
        {
            service = Connect();
            task = TryGet(service, taskPath) ?? throw new FileNotFoundException("Owned startup task is missing.");
            running = Invoke(task, "Run", null!);
        }
        finally { Release(running); Release(task); Release(service); }
    }

    public void DeleteOwnedStartupTests()
    {
        object? service = null;
        try
        {
            service = Connect();
            DeleteTestsInFolder(service, Identity.PreferredFolderPath);
            DeleteTestsInFolder(service, "\\");
            TryDeletePreferredFolder(service);
        }
        finally { Release(service); }
    }

    private void TryDeletePreferredFolder(object service)
    {
        object? root = null;
        try
        {
            root = Invoke(service, "GetFolder", "\\");
            _ = Invoke(root, "DeleteFolder", Identity.PreferredFolderPath.TrimStart('\\'), 0);
        }
        catch (COMException exception) when (IsMissing(exception) || (uint)exception.HResult == 0x80070091u) { }
        finally { Release(root); }
    }

    private void DeleteTestsInFolder(object service, string folderPath)
    {
        object? folder = null; object? tasks = null;
        try
        {
            try { folder = Invoke(service, "GetFolder", folderPath); }
            catch (COMException exception) when (IsMissing(exception)) { return; }
            tasks = Invoke(folder, "GetTasks", 1);
            var paths = new List<string>();
            for (var index = 1; index <= ToInt(Get(tasks, "Count")); index++)
            {
                object? task = null;
                try
                {
                    task = Invoke(tasks, "Item", index);
                    var path = Text(Get(task, "Path"));
                    if (Identity.IsOwnedTestPath(path)) paths.Add(path);
                }
                finally { Release(task); }
            }
            foreach (var path in paths) DeleteAtPath(path);
        }
        finally { Release(tasks); Release(folder); }
    }

    private static ScheduledStartupDefinition ReadDefinition(object task)
    {
        object? definition = null; object? triggers = null; object? trigger = null; object? actions = null;
        object? action = null; object? settings = null; object? principal = null;
        try
        {
            definition = Get(task, "Definition"); triggers = Get(definition, "Triggers"); actions = Get(definition, "Actions");
            settings = Get(definition, "Settings"); principal = Get(definition, "Principal");
            var triggerCount = ToInt(Get(triggers, "Count")); var actionCount = ToInt(Get(actions, "Count"));
            if (triggerCount > 0) trigger = Invoke(triggers, "Item", 1);
            if (actionCount > 0) action = Invoke(actions, "Item", 1);
            var taskPath = Text(Get(task, "Path"));
            return new(taskPath, Text(Get(action, "Path")), Text(Get(action, "Arguments")), Text(Get(action, "WorkingDirectory")),
                Text(Get(principal, "UserId")), ToBool(Get(task, "Enabled")), trigger is not null && ToInt(Get(trigger, "Type")) == 9,
                ToInt(Get(principal, "LogonType")) == 3, ToInt(Get(principal, "RunLevel")) == 0,
                ToInt(Get(settings, "MultipleInstances")) == 2,
                !ToBool(Get(settings, "DisallowStartIfOnBatteries")) && !ToBool(Get(settings, "StopIfGoingOnBatteries")),
                ToBool(Get(settings, "RunOnlyIfIdle")), ToBool(Get(settings, "RunOnlyIfNetworkAvailable")), ToBool(Get(settings, "WakeToRun")),
                ToBool(Get(settings, "AllowDemandStart")), Text(Get(settings, "ExecutionTimeLimit")), ToInt(Get(settings, "RestartCount")),
                Text(Get(settings, "RestartInterval")), ToDate(Get(task, "LastRunTime")), ToInt(Get(task, "LastTaskResult")),
                OwnedStartupTaskIdentity.SplitTaskPath(taskPath).Name, triggerCount, actionCount,
                action is null ? -1 : ToInt(Get(action, "Type")), trigger is not null && ToBool(Get(trigger, "Enabled")),
                Text(Get(trigger, "UserId")), ToBool(Get(settings, "Enabled")));
        }
        finally
        { Release(principal); Release(settings); Release(action); Release(actions); Release(trigger); Release(triggers); Release(definition); }
    }

    private static object Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("Task Scheduler COM is unavailable.");
        var service = Activator.CreateInstance(type)!;
        try { _ = Invoke(service, "Connect"); return service; }
        catch { Release(service); throw; }
    }
    private static object? TryGet(object service, string path)
    {
        var (folderPath, name) = OwnedStartupTaskIdentity.SplitTaskPath(path); object? folder = null;
        try { folder = Invoke(service, "GetFolder", folderPath); return Invoke(folder, "GetTask", name); }
        catch (COMException exception) when (IsMissing(exception)) { return null; }
        finally { Release(folder); }
    }
    private static bool IsMissing(COMException e) => (uint)e.HResult is 0x80070002u or 0x80070003u;
    private static bool IsFolderRegistrationFailure(COMException e) => IsMissing(e) || (uint)e.HResult is 0x80070005u;
    private static object Invoke(object target, string name, params object?[] arguments) =>
        target.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, target, arguments, CultureInfo.InvariantCulture)!;
    private static object Get(object? target, string name) => target is null ? string.Empty :
        target.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture)!;
    private static void Set(object target, string name, object value) => target.GetType().InvokeMember(name,
        System.Reflection.BindingFlags.SetProperty, null, target, [value], CultureInfo.InvariantCulture);
    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    private static int ToInt(object? value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);
    private static bool ToBool(object? value) => Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    private static DateTimeOffset? ToDate(object? value) => value is DateTime date && date.Year > 1900 ? new DateTimeOffset(date) : null;
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}

public sealed class WindowsTaskSchedulerStartupBackend(
    ITaskSchedulerStore store,
    ApplicationExecutionEnvironment environment) : IStartupRegistrationBackend
{
    public StartupRegistrationBackendKind Kind => StartupRegistrationBackendKind.TaskScheduler;
    private static string Exe => Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unavailable."));
    private static OwnedStartupTaskIdentity Identity => OwnedStartupTaskIdentity.Create();
    private static ScheduledStartupDefinition Desired => new(Identity.PreferredTaskPath, Exe, "--startup --startup-source TaskScheduler",
        Path.GetDirectoryName(Exe)!, Identity.CurrentUserSid, true, true, true, true, true, true, false, false, false,
        true, "PT0S", 3, "PT1M", TaskName: Identity.PreferredTaskName, TriggerUserId: Identity.CurrentUserSid);

    public Task<StartupRegistrationState> GetStateAsync(CancellationToken token = default) => Task.Run(() =>
    {
        if (!environment.AllowWindowsStartupRegistration)
            return State(StartupRegistrationHealth.SchedulerUnavailable, "startup.environmentDisabled");
        try
        {
            var owned = store.CaptureOwned();
            if (owned.SchedulerState == OwnedTaskSchedulerState.Unavailable)
                return State(StartupRegistrationHealth.SchedulerUnavailable, "startup.schedulerUnavailable");
            if (owned.SchedulerState == OwnedTaskSchedulerState.PartialFailure)
                return State(StartupRegistrationHealth.UnknownFailure, "startup.taskUnknown");
            if (owned.HasBoth)
                return new(true, Kind, StartupRegistrationHealth.MultipleTaskSchedulerRegistrations,
                    DiagnosticCode: "startup.multipleTaskSchedulerRegistrations", Presence: StartupRegistrationPresence.Present);
            var actual = owned.PreferredDefinition ?? owned.FallbackDefinition;
            if (actual is null) return State(StartupRegistrationHealth.Missing, "startup.taskMissing");
            var desired = Desired;
            var ownedPath = Identity.OwnedPaths.Any(path => string.Equals(path, actual.TaskPath, StringComparison.OrdinalIgnoreCase));
            var path = PathEquals(actual.ExecutablePath, desired.ExecutablePath);
            var args = actual.Arguments == desired.Arguments;
            var work = PathEquals(actual.WorkingDirectory, desired.WorkingDirectory);
            var trigger = actual.TriggerCount == 1 && actual.LogonTrigger && actual.TriggerEnabled &&
                SameUser(actual.TriggerUserId, Identity.CurrentUserSid);
            var principal = actual.InteractiveToken && actual.LeastPrivilege && SameUser(actual.UserId, Identity.CurrentUserSid);
            var settings = actual.TaskSettingsEnabled && actual.IgnoreNew && actual.AllowOnBatteries && !actual.RunOnlyIfIdle &&
                !actual.RunOnlyIfNetworkAvailable && !actual.WakeToRun && actual.AllowStartOnDemand &&
                actual.ExecutionTimeLimit == "PT0S" && actual.RestartCount == 3 && actual.RestartInterval == "PT1M";
            var action = actual.ActionCount == 1 && actual.ActionType == 0;
            var health = !actual.Enabled ? StartupRegistrationHealth.DisabledExternally :
                ownedPath && path && args && work && trigger && principal && settings && action ? StartupRegistrationHealth.Healthy :
                ownedPath && !path && args && work && trigger && principal && settings && action ? StartupRegistrationHealth.ExecutableMoved :
                StartupRegistrationHealth.DefinitionChanged;
            return new(true, Kind, health, path, args, work, trigger, principal, settings && action, actual.Enabled,
                actual.LastRunTime, actual.LastTaskResult, health switch
                {
                    StartupRegistrationHealth.Healthy => "startup.taskHealthy",
                    StartupRegistrationHealth.DisabledExternally => "startup.taskDisabledExternally",
                    StartupRegistrationHealth.ExecutableMoved => "startup.taskExecutableMoved",
                    _ => "startup.taskChanged"
                }, StartupRegistrationState.PresenceFor(health));
        }
        catch (UnauthorizedAccessException) { return State(StartupRegistrationHealth.AccessDenied, "startup.taskAccessDenied"); }
        catch (COMException) { return State(StartupRegistrationHealth.SchedulerUnavailable, "startup.schedulerUnavailable"); }
        catch (Exception exception) when (exception is IOException or System.Security.SecurityException)
        { return State(StartupRegistrationHealth.UnknownFailure, "startup.taskUnknown"); }
    }, token);

    public async Task<StartupRegistrationState> EnableAsync(CancellationToken token = default)
    {
        var info = new FileInfo(Exe);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Executable must exist and cannot be a reparse point.");
        await Task.Run(() => store.Register(Desired), token);
        var created = store.CaptureOwned();
        if (created.PreferredDefinition is not null && created.FallbackDefinition is not null)
            await Task.Run(() => store.DeleteAtPath(Identity.FallbackTaskPath), token);
        return await GetStateAsync(token);
    }
    public Task DisableAsync(CancellationToken token = default) => Task.Run(store.Delete, token);
    public OwnedStartupTaskSnapshot Capture() => store.CaptureOwned();
    public void Restore(OwnedStartupTaskSnapshot snapshot)
    {
        if (snapshot.SchedulerState != OwnedTaskSchedulerState.Available)
            throw new IOException("An unavailable Scheduler snapshot cannot be restored exactly.");
        RestorePath(Identity.PreferredTaskPath, snapshot.PreferredDefinition);
        RestorePath(Identity.FallbackTaskPath, snapshot.FallbackDefinition);
        var verified = store.CaptureOwned();
        if (!SnapshotsEquivalent(verified, snapshot))
            throw new IOException("Owned Task Scheduler snapshot rollback verification failed.");
    }
    private void RestorePath(string path, ScheduledStartupDefinition? definition)
    { if (definition is null) store.DeleteAtPath(path); else store.RegisterAtPath(path, definition); }
    public static bool SnapshotsEquivalent(OwnedStartupTaskSnapshot left, OwnedStartupTaskSnapshot right) =>
        left.SchedulerState == OwnedTaskSchedulerState.Available &&
        right.SchedulerState == OwnedTaskSchedulerState.Available &&
        DefinitionsEquivalent(left.PreferredDefinition, right.PreferredDefinition) &&
        DefinitionsEquivalent(left.FallbackDefinition, right.FallbackDefinition);
    private static bool DefinitionsEquivalent(ScheduledStartupDefinition? left, ScheduledStartupDefinition? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left with { LastRunTime = null, LastTaskResult = null } ==
               right with { LastRunTime = null, LastTaskResult = null };
    }
    public Task<StartupRegistrationState> RepairAsync(CancellationToken token = default) => EnableAsync(token);
    public async Task<StartupRegistrationState> RunTestAsync(CancellationToken token = default)
    {
        var before = store.CaptureOwned();
        var path = before.PreferredDefinition?.TaskPath ?? before.FallbackDefinition?.TaskPath ??
            throw new FileNotFoundException("Owned startup task is missing.");
        await Task.Run(() => store.RunAtPath(path), token);
        for (var attempt = 0; attempt < 25; attempt++)
        {
            await Task.Delay(200, token); var current = store.CaptureOwned();
            var beforeTask = before.PreferredDefinition ?? before.FallbackDefinition;
            var currentTask = current.PreferredDefinition ?? current.FallbackDefinition;
            if (currentTask?.LastRunTime != beforeTask?.LastRunTime || currentTask?.LastTaskResult != beforeTask?.LastTaskResult)
                return await GetStateAsync(token);
        }
        return (await GetStateAsync(token)) with { Health = StartupRegistrationHealth.PartialFailure, DiagnosticCode = "startup.testTimedOut" };
    }
    private StartupRegistrationState State(StartupRegistrationHealth health, string code) =>
        new(false, Kind, health, DiagnosticCode: code, Presence: StartupRegistrationState.PresenceFor(health));
    private static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
    private static bool SameUser(string value, string sid)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var candidate = value.StartsWith("S-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(value) : (SecurityIdentifier)new NTAccount(value).Translate(typeof(SecurityIdentifier));
            return string.Equals(candidate.Value, sid, StringComparison.OrdinalIgnoreCase);
        }
        catch (IdentityNotMappedException) { return false; }
        catch (ArgumentException) { return false; }
    }
}

public sealed record WindowsStartupRegistrationSnapshot(
    StartupRegistrationState TaskSchedulerState,
    StartupRegistrationState RegistryRunState,
    StartupRegistrationBackendKind ConfiguredBackend,
    StartupRegistrationBackendKind EffectiveBackend,
    StartupRegistrationHealth OverallHealth,
    bool CleanupPending,
    string DiagnosticCode);

public sealed class WindowsStartupRegistrationService(
    UserSettingsService settingsService,
    WindowsTaskSchedulerStartupBackend scheduler,
    WindowsRunStartupBackend registry,
    ApplicationExecutionEnvironment environment,
    IStartupTestCoordinator? startupTestCoordinator = null)
{
    public bool IsAvailable => environment.AllowWindowsStartupRegistration;

    public async Task<WindowsStartupRegistrationSnapshot> GetSnapshotAsync(CancellationToken token = default)
    {
        if (!IsAvailable)
        {
            var disabled = new StartupRegistrationState(false, StartupRegistrationBackendKind.None,
                StartupRegistrationHealth.Disabled, DiagnosticCode: "startup.environmentDisabled",
                Presence: StartupRegistrationPresence.Absent);
            return new(disabled, disabled, StartupRegistrationBackendKind.None, StartupRegistrationBackendKind.None,
                StartupRegistrationHealth.Disabled, false, disabled.DiagnosticCode);
        }
        var settingsTask = settingsService.ReadAsync(token);
        var taskStateTask = scheduler.GetStateAsync(token);
        var runStateTask = registry.GetStateAsync(token);
        await Task.WhenAll(settingsTask, taskStateTask, runStateTask);
        var settings = await settingsTask; var task = await taskStateTask; var run = await runStateTask;
        var configured = ParseBackend(settings.StartupRegistrationBackend);
        var presentCount = (task.Presence == StartupRegistrationPresence.Present ? 1 : 0) +
                           (run.Presence == StartupRegistrationPresence.Present ? 1 : 0);
        if (presentCount == 2)
            return new(task, run, configured, task.MatchesCurrentExecutable ? task.Backend : run.Backend,
                StartupRegistrationHealth.MultipleRegistrations, settings.StartupRegistrationCleanupPending, "startup.multipleRegistrations");
        if (task.Health == StartupRegistrationHealth.DisabledExternally)
            return new(task, run, configured, task.Backend, task.Health, settings.StartupRegistrationCleanupPending, task.DiagnosticCode);
        if (run.MatchesCurrentExecutable && task.Presence == StartupRegistrationPresence.Unknown)
            return new(task, run, configured, run.Backend, StartupRegistrationHealth.RegistryFallbackWithTaskStateUnknown,
                settings.StartupRegistrationCleanupPending, "startup.registryFallbackTaskUnknown");
        var effective = task.Presence == StartupRegistrationPresence.Present ? task :
            run.Presence == StartupRegistrationPresence.Present ? run :
            configured == StartupRegistrationBackendKind.RegistryRun ? run : task;
        var health = effective.Health;
        var code = effective.DiagnosticCode;
        if (configured != StartupRegistrationBackendKind.None && effective.Backend != configured && effective.Presence == StartupRegistrationPresence.Present)
        { health = StartupRegistrationHealth.ConfigurationDrift; code = "startup.configurationDrift"; }
        return new(task, run, configured, effective.Backend, health, settings.StartupRegistrationCleanupPending, code);
    }

    public async Task<StartupRegistrationState> GetStateAsync(CancellationToken token = default)
    {
        var snapshot = await GetSnapshotAsync(token);
        var state = snapshot.EffectiveBackend == StartupRegistrationBackendKind.RegistryRun
            ? snapshot.RegistryRunState : snapshot.TaskSchedulerState;
        return state with { ConfiguredByUser = snapshot.ConfiguredBackend != StartupRegistrationBackendKind.None,
            Health = snapshot.OverallHealth, DiagnosticCode = snapshot.DiagnosticCode };
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken token = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("Startup registration is unavailable in this environment.");
        var originalSettings = await settingsService.ReadAsync(token);
        OwnedStartupTaskSnapshot? originalTask = null;
        try { originalTask = scheduler.Capture(); }
        catch (COMException) { }
        var originalRun = registry.Capture();
        if (!enabled)
        {
            await DisableTransactionalAsync(originalSettings, token);
            return;
        }

        try
        {
            StartupRegistrationState state;
            try { state = await scheduler.EnableAsync(token); }
            catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or IOException)
            {
                if (originalTask is null || originalTask.SchedulerState != OwnedTaskSchedulerState.Available)
                    throw new IOException("Task Scheduler state is unknown; Registry fallback was not created.", exception);
                var afterFailure = scheduler.Capture();
                if (!WindowsTaskSchedulerStartupBackend.SnapshotsEquivalent(afterFailure, originalTask))
                {
                    scheduler.Restore(originalTask);
                    if (!WindowsTaskSchedulerStartupBackend.SnapshotsEquivalent(scheduler.Capture(), originalTask))
                        throw new IOException("Task Scheduler partial success could not be rolled back safely.", exception);
                }
                state = await registry.EnableAsync(token);
                if (state.Health != StartupRegistrationHealth.HealthyRegistryFallback)
                    throw new IOException("Registry fallback verification failed.");
                await SaveSettingsAsync(true, StartupRegistrationBackendKind.RegistryRun, false, token);
                return;
            }
            if (state.Health != StartupRegistrationHealth.Healthy)
                throw new IOException("Task definition verification failed.");
            await registry.DisableAsync(token);
            if ((await registry.GetStateAsync(token)).Presence != StartupRegistrationPresence.Absent)
                throw new IOException("Registry Run migration cleanup could not be verified.");
            await SaveSettingsAsync(true, StartupRegistrationBackendKind.TaskScheduler, false, token);
        }
        catch
        {
            await RestoreAsync(originalTask?.SchedulerState == OwnedTaskSchedulerState.Available ? originalTask : null,
                originalRun, originalSettings, token);
            throw;
        }
    }

    private async Task DisableTransactionalAsync(UserSettings originalSettings, CancellationToken token)
    {
        Exception? failure = null;
        try { await scheduler.DisableAsync(token); } catch (Exception e) { failure = e; }
        try { await registry.DisableAsync(token); } catch (Exception e) { failure ??= e; }
        var task = await scheduler.GetStateAsync(token); var run = await registry.GetStateAsync(token);
        var confirmed = task.Presence == StartupRegistrationPresence.Absent && run.Presence == StartupRegistrationPresence.Absent;
        var cleanupPending = !confirmed;
        await SaveSettingsAsync(false, StartupRegistrationBackendKind.None, cleanupPending, token);
        if (failure is not null || !confirmed)
            throw new IOException("One or more startup registrations could not be removed.", failure);
    }

    public async Task<StartupRegistrationState> RepairAsync(CancellationToken token = default)
    {
        var snapshot = await GetSnapshotAsync(token);
        if (snapshot.CleanupPending)
        {
            await SetEnabledAsync(false, token);
            return await GetStateAsync(token);
        }
        await SetEnabledAsync(true, token);
        return await GetStateAsync(token);
    }

    public async Task<StartupRegistrationState> RunTestAsync(CancellationToken token = default)
    {
        var snapshot = await GetSnapshotAsync(token);
        return snapshot.OverallHealth == StartupRegistrationHealth.Healthy &&
               snapshot.EffectiveBackend == StartupRegistrationBackendKind.TaskScheduler
            ? startupTestCoordinator is not null
                ? await startupTestCoordinator.RunAsync(token)
                : await scheduler.RunTestAsync(token)
            : new(false, snapshot.EffectiveBackend, StartupRegistrationHealth.PartialFailure,
                DiagnosticCode: "startup.testRequiresHealthyTask", Presence: StartupRegistrationPresence.Unknown);
    }

    public async Task ReconcileAsync(UserSettings settings, CancellationToken token = default)
    {
        if (!IsAvailable) return;
        if (settings.StartupRegistrationCleanupPending)
        {
            try { await SetEnabledAsync(false, token); } catch (IOException) { }
            return;
        }
        if (!settings.LaunchAtLogin) return;
        var snapshot = await GetSnapshotAsync(token);
        if (snapshot.TaskSchedulerState.Health == StartupRegistrationHealth.DisabledExternally) return;
        if (snapshot.EffectiveBackend == StartupRegistrationBackendKind.TaskScheduler &&
            snapshot.TaskSchedulerState.Health == StartupRegistrationHealth.ExecutableMoved &&
            snapshot.TaskSchedulerState.ArgumentsMatch && snapshot.TaskSchedulerState.TriggerMatches &&
            snapshot.TaskSchedulerState.PrincipalMatches && snapshot.TaskSchedulerState.SettingsMatch)
            await SetEnabledAsync(true, token);
    }

    private async Task RestoreAsync(OwnedStartupTaskSnapshot? task, string? run, UserSettings settings, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            if (task is not null) await Task.Run(() => scheduler.Restore(task), token);
        }
        catch (Exception e) { failure = e; }
        try
        {
            await Task.Run(() => registry.Restore(run), token);
        }
        catch (Exception e) { failure ??= e; }
        try { await SaveSettingsAsync(settings.LaunchAtLogin, ParseBackend(settings.StartupRegistrationBackend),
            settings.StartupRegistrationCleanupPending, token); }
        catch (Exception e) { failure ??= e; }
        if (failure is not null) throw new IOException("Startup registration rollback was incomplete.", failure);
    }

    private Task SaveSettingsAsync(bool enabled, StartupRegistrationBackendKind backend, bool cleanupPending, CancellationToken token) =>
        settingsService.UpdateAsync(settings =>
        {
            settings.LaunchAtLogin = enabled;
            settings.StartupRegistrationBackend = backend.ToString();
            settings.StartupRegistrationCleanupPending = cleanupPending;
        }, token);

    private static StartupRegistrationBackendKind ParseBackend(string value) =>
        Enum.TryParse<StartupRegistrationBackendKind>(value, out var backend) ? backend : StartupRegistrationBackendKind.None;
}
