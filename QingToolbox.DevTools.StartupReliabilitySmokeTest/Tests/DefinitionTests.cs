using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;
using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;
internal static class DefinitionTests
{
    public static async Task RunAsync()
    {
        var store = new FakeTaskStore(); var backend = new WindowsTaskSchedulerStartupBackend(store, ApplicationExecutionEnvironment.Production());
        AssertEx.True((await backend.GetStateAsync()).Presence == StartupRegistrationPresence.Absent, "Missing must be Absent.");
        var healthy = await backend.EnableAsync();
        AssertEx.True(healthy.Health == StartupRegistrationHealth.Healthy && healthy.Presence == StartupRegistrationPresence.Present, "Healthy task rejected.");
        store.Definition = store.Definition! with { TriggerCount = 2 };
        AssertEx.True((await backend.GetStateAsync()).Health == StartupRegistrationHealth.DefinitionChanged, "Extra trigger accepted.");
        store.Definition = store.Definition! with { TriggerCount = 1, ActionCount = 2 };
        AssertEx.True((await backend.GetStateAsync()).Health == StartupRegistrationHealth.DefinitionChanged, "Extra action accepted.");
        store.Definition = store.Definition! with { ActionCount = 1, TriggerEnabled = false };
        AssertEx.True((await backend.GetStateAsync()).Health == StartupRegistrationHealth.DefinitionChanged, "Disabled trigger accepted.");
        store.Definition = store.Definition! with { TriggerEnabled = true, Enabled = false };
        AssertEx.True((await backend.GetStateAsync()).Health == StartupRegistrationHealth.DisabledExternally, "External disable hidden.");
        store.Unavailable = true;
        var unknown = await backend.GetStateAsync();
        AssertEx.True(unknown.Presence == StartupRegistrationPresence.Unknown && !unknown.IsRegistered, "Scheduler unavailable was reported present.");
    }
}
