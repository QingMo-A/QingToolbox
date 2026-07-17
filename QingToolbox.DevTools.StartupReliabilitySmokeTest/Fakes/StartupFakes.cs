using System.Runtime.InteropServices;
using QingToolbox.Shell.Services;

namespace StartupReliabilitySmokeTest.Fakes;

internal sealed class FakeRunStore : IStartupRegistrationStore
{
    public string? Value { get; set; }
    public bool FailWrite { get; set; }
    public bool FailDelete { get; set; }
    public string? Read() => Value;
    public void Write(string command) { if (FailWrite) throw new IOException("write"); Value = command; }
    public void Delete() { if (FailDelete) throw new IOException("delete"); Value = null; }
}

internal sealed class FakeTaskStore : ITaskSchedulerStore
{
    private static readonly OwnedStartupTaskIdentity Identity = OwnedStartupTaskIdentity.Create();
    private readonly Dictionary<string, (ScheduledStartupDefinition Definition, string Xml)> _tasks =
        new(StringComparer.OrdinalIgnoreCase);
    public ScheduledStartupDefinition? PreferredDefinition
    { get => Get(Identity.PreferredTaskPath); set => Set(Identity.PreferredTaskPath, value); }
    public ScheduledStartupDefinition? FallbackDefinition
    { get => Get(Identity.FallbackTaskPath); set => Set(Identity.FallbackTaskPath, value); }
    public ScheduledStartupDefinition? Definition
    {
        get => PreferredDefinition ?? FallbackDefinition;
        set { PreferredDefinition = value; FallbackDefinition = null; }
    }
    public bool Unavailable { get; set; }
    public bool FailRegister { get; set; }
    public bool FailDelete { get; set; }
    public int RegisterCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int RunCount { get; private set; }
    public int TaskCount => _tasks.Count;
    public Action<string>? OnRun { get; set; }
    public bool FailAfterRegister { get; set; }
    public bool FailAfterRegisterWithCom { get; set; }
    public bool FailRestore { get; set; }
    public TimeSpan RestoreDelay { get; set; }
    public ManualResetEventSlim? RestoreStarted { get; set; }
    public ManualResetEventSlim? ReleaseRestore { get; set; }
    public Action? OnRegistered { get; set; }
    public string? PreferredXml { get => GetXml(Identity.PreferredTaskPath); set => SetXml(Identity.PreferredTaskPath, value); }
    public string? FallbackXml { get => GetXml(Identity.FallbackTaskPath); set => SetXml(Identity.FallbackTaskPath, value); }
    public OwnedStartupTaskSnapshot CaptureOwned()
    {
        if (Unavailable) return new(null, null, OwnedTaskSchedulerState.Unavailable);
        return new(CaptureAtPath(Identity.PreferredTaskPath), CaptureAtPath(Identity.FallbackTaskPath), OwnedTaskSchedulerState.Available);
    }
    public void Register(ScheduledStartupDefinition definition)
    {
        if (Unavailable) throw new COMException("unavailable");
        if (FailRegister) throw new IOException("register");
        RegisterCount++;
        if (definition.TaskPath.Equals(Identity.FallbackTaskPath, StringComparison.OrdinalIgnoreCase))
            Set(Identity.FallbackTaskPath, definition);
        else
            Set(Identity.PreferredTaskPath, definition with { TaskPath = Identity.PreferredTaskPath });
        if (FailAfterRegisterWithCom) throw new COMException("after-register");
        if (FailAfterRegister) throw new IOException("after-register");
        OnRegistered?.Invoke();
    }
    public void RegisterAtPath(string taskPath, ScheduledStartupDefinition definition)
    {
        RegisterCount++;
        if (taskPath.Equals(Identity.PreferredTaskPath, StringComparison.OrdinalIgnoreCase))
            Set(taskPath, definition with { TaskPath = taskPath });
        else if (taskPath.Equals(Identity.FallbackTaskPath, StringComparison.OrdinalIgnoreCase) || Identity.IsOwnedTestPath(taskPath))
            Set(taskPath, definition with { TaskPath = taskPath });
        else throw new ArgumentException("not owned", nameof(taskPath));
        if (FailAfterRegisterWithCom) throw new COMException("after-register");
        if (FailAfterRegister) throw new IOException("after-register");
        OnRegistered?.Invoke();
    }
    public OwnedTaskDefinitionSnapshot? CaptureAtPath(string taskPath)
    {
        if (Unavailable) throw new COMException("unavailable");
        return _tasks.TryGetValue(taskPath, out var value)
            ? new(taskPath, value.Xml, value.Definition.Enabled, value.Definition.LastRunTime,
                value.Definition.LastTaskResult, value.Definition)
            : null;
    }
    public void RestoreAtPath(string taskPath, OwnedTaskDefinitionSnapshot snapshot)
    {
        RestoreStarted?.Set();
        ReleaseRestore?.Wait();
        if (RestoreDelay > TimeSpan.Zero) Thread.Sleep(RestoreDelay);
        if (FailRestore) throw new IOException("restore");
        _tasks[taskPath] = (snapshot.HealthDefinition with { TaskPath = taskPath, Enabled = snapshot.Enabled }, snapshot.DefinitionXml);
    }
    public void Delete()
    {
        if (Unavailable) throw new COMException("unavailable");
        if (FailDelete) throw new IOException("delete");
        DeleteCount++;
        _tasks.Remove(Identity.PreferredTaskPath);
        _tasks.Remove(Identity.FallbackTaskPath);
    }
    public void DeleteAtPath(string taskPath)
    {
        if (FailDelete) throw new IOException("delete");
        DeleteCount++;
        if (taskPath.Equals(Identity.PreferredTaskPath, StringComparison.OrdinalIgnoreCase) ||
            taskPath.Equals(Identity.FallbackTaskPath, StringComparison.OrdinalIgnoreCase) || Identity.IsOwnedTestPath(taskPath)) _tasks.Remove(taskPath);
        else throw new ArgumentException("not owned", nameof(taskPath));
    }
    public void RunAtPath(string taskPath)
    {
        if (Unavailable) throw new COMException("unavailable");
        var definition = Get(taskPath);
        if (definition is null) throw new FileNotFoundException();
        RunCount++;
        OnRun?.Invoke(taskPath);
        var updated = definition with { LastRunTime = DateTimeOffset.UtcNow, LastTaskResult = 0 };
        Set(taskPath, updated, preserveXml: true);
    }
    public void DeleteOwnedStartupTests() { }
    private ScheduledStartupDefinition? Get(string path) => _tasks.TryGetValue(path, out var value) ? value.Definition : null;
    private string? GetXml(string path) => _tasks.TryGetValue(path, out var value) ? value.Xml : null;
    private void Set(string path, ScheduledStartupDefinition? definition, bool preserveXml = false)
    {
        if (definition is null) { _tasks.Remove(path); return; }
        var xml = preserveXml && _tasks.TryGetValue(path, out var current) ? current.Xml : CreateXml(definition);
        _tasks[path] = (definition with { TaskPath = path }, xml);
    }
    private void SetXml(string path, string? xml)
    {
        if (xml is null || !_tasks.TryGetValue(path, out var current)) return;
        _tasks[path] = (current.Definition, xml);
    }
    private static string CreateXml(ScheduledStartupDefinition definition) =>
        $"<Task><Enabled>{definition.Enabled}</Enabled><Triggers Count=\"{definition.TriggerCount}\"/><Actions Count=\"{definition.ActionCount}\"/></Task>";
}

internal static class AssertEx
{
    public static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task ThrowsAsync(Func<Task> action, string message)
    { try { await action(); } catch { return; } throw new InvalidOperationException(message); }
}

internal sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
