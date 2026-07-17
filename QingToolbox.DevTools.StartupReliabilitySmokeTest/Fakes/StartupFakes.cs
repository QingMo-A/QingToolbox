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
    public ScheduledStartupDefinition? PreferredDefinition { get; set; }
    public ScheduledStartupDefinition? FallbackDefinition { get; set; }
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
    public Action<string>? OnRun { get; set; }
    public bool FailAfterRegister { get; set; }
    public OwnedStartupTaskSnapshot CaptureOwned()
    {
        if (Unavailable) return new(null, null, OwnedTaskSchedulerState.Unavailable);
        return new(PreferredDefinition, FallbackDefinition, OwnedTaskSchedulerState.Available);
    }
    public void Register(ScheduledStartupDefinition definition)
    {
        if (Unavailable) throw new COMException("unavailable");
        if (FailRegister) throw new IOException("register");
        RegisterCount++;
        if (definition.TaskPath.Equals(Identity.FallbackTaskPath, StringComparison.OrdinalIgnoreCase))
            FallbackDefinition = definition;
        else
            PreferredDefinition = definition with { TaskPath = Identity.PreferredTaskPath };
        if (FailAfterRegister) throw new IOException("after-register");
    }
    public void RegisterAtPath(string taskPath, ScheduledStartupDefinition definition)
    {
        RegisterCount++;
        if (taskPath.Equals(Identity.PreferredTaskPath, StringComparison.OrdinalIgnoreCase))
            PreferredDefinition = definition with { TaskPath = taskPath };
        else if (taskPath.Equals(Identity.FallbackTaskPath, StringComparison.OrdinalIgnoreCase) || Identity.IsOwnedTestPath(taskPath))
            FallbackDefinition = definition with { TaskPath = taskPath };
        else throw new ArgumentException("not owned", nameof(taskPath));
    }
    public void Delete()
    {
        if (Unavailable) throw new COMException("unavailable");
        if (FailDelete) throw new IOException("delete");
        DeleteCount++;
        PreferredDefinition = null;
        FallbackDefinition = null;
    }
    public void DeleteAtPath(string taskPath)
    {
        if (FailDelete) throw new IOException("delete");
        DeleteCount++;
        if (taskPath.Equals(Identity.PreferredTaskPath, StringComparison.OrdinalIgnoreCase)) PreferredDefinition = null;
        else if (taskPath.Equals(Identity.FallbackTaskPath, StringComparison.OrdinalIgnoreCase) || Identity.IsOwnedTestPath(taskPath)) FallbackDefinition = null;
        else throw new ArgumentException("not owned", nameof(taskPath));
    }
    public void RunAtPath(string taskPath)
    {
        if (Unavailable) throw new COMException("unavailable");
        var definition = taskPath.Equals(Identity.PreferredTaskPath, StringComparison.OrdinalIgnoreCase)
            ? PreferredDefinition : FallbackDefinition;
        if (definition is null) throw new FileNotFoundException();
        RunCount++;
        OnRun?.Invoke(taskPath);
        var updated = definition with { LastRunTime = DateTimeOffset.UtcNow, LastTaskResult = 0 };
        if (taskPath.Equals(Identity.PreferredTaskPath, StringComparison.OrdinalIgnoreCase)) PreferredDefinition = updated;
        else FallbackDefinition = updated;
    }
    public void DeleteOwnedStartupTests() { }
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
