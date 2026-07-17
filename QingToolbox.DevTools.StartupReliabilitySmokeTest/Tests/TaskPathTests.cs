using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;
using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;
internal static class TaskPathTests
{
    public static async Task RunAsync()
    {
        var identity = OwnedStartupTaskIdentity.Create("S-1-5-21-1-2-3-1001");
        var preferred = OwnedStartupTaskIdentity.SplitTaskPath(identity.PreferredTaskPath);
        var fallback = OwnedStartupTaskIdentity.SplitTaskPath(identity.FallbackTaskPath);
        AssertEx.True(preferred.Folder == "\\QingToolbox" && preferred.Name == identity.PreferredTaskName, "Preferred task split failed.");
        AssertEx.True(fallback.Folder == "\\" && fallback.Name == identity.FallbackTaskName, "Root fallback split failed.");
        foreach (var invalid in new[] { "", "Task", "\\", "\\Task\\", "\\..", "\\Folder\\..", "\\Bad\u0001Task" })
        { try { OwnedStartupTaskIdentity.SplitTaskPath(invalid); throw new InvalidOperationException($"Invalid task path accepted: {invalid}"); } catch (ArgumentException) { } }
        var executable = Environment.ProcessPath!;
        ScheduledStartupDefinition Definition(string path) => new(path, executable,
            "--startup --startup-source TaskScheduler", Path.GetDirectoryName(executable)!, identity.CurrentUserSid,
            true, true, true, true, true, true, false, false, false, true, "PT0S", 3, "PT1M",
            TaskName: OwnedStartupTaskIdentity.SplitTaskPath(path).Name, TriggerUserId: identity.CurrentUserSid);
        var store = new FakeTaskStore
        {
            PreferredDefinition = Definition(identity.PreferredTaskPath),
            FallbackDefinition = Definition(identity.FallbackTaskPath)
        };
        var backend = new WindowsTaskSchedulerStartupBackend(store, ApplicationExecutionEnvironment.Production());
        var duplicate = await backend.GetStateAsync();
        AssertEx.True(duplicate.Health == StartupRegistrationHealth.MultipleTaskSchedulerRegistrations,
            "Preferred and fallback tasks were not reported as an internal Scheduler duplicate.");
        await backend.RepairAsync();
        AssertEx.True(store.PreferredDefinition is not null && store.FallbackDefinition is null,
            "Repair did not leave only the preferred task.");
    }
}
