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
    public ScheduledStartupDefinition? Definition { get; set; }
    public bool Unavailable { get; set; }
    public bool FailRegister { get; set; }
    public bool FailDelete { get; set; }
    public int RegisterCount { get; private set; }
    public int DeleteCount { get; private set; }
    public int RunCount { get; private set; }
    public ScheduledStartupDefinition? Read()
    {
        if (Unavailable) throw new COMException("unavailable");
        return Definition;
    }
    public void Register(ScheduledStartupDefinition definition)
    {
        if (Unavailable) throw new COMException("unavailable");
        if (FailRegister) throw new IOException("register");
        RegisterCount++;
        Definition = definition;
    }
    public void Delete()
    {
        if (Unavailable) throw new COMException("unavailable");
        if (FailDelete) throw new IOException("delete");
        DeleteCount++;
        Definition = null;
    }
    public void Run()
    {
        if (Unavailable) throw new COMException("unavailable");
        if (Definition is null) throw new FileNotFoundException();
        RunCount++;
        Definition = Definition with { LastRunTime = DateTimeOffset.UtcNow, LastTaskResult = 0 };
    }
}

internal static class AssertEx
{
    public static void True(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static async Task ThrowsAsync(Func<Task> action, string message)
    { try { await action(); } catch { return; } throw new InvalidOperationException(message); }
}
