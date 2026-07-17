using QingToolbox.Core.Settings;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;
using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;
internal static class TransactionTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "QingToolbox-transaction-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var settings = new UserSettingsService(Path.Combine(root, "settings.json"));
            var task = new FakeTaskStore(); var run = new FakeRunStore { Value = "legacy-command" };
            var service = new WindowsStartupRegistrationService(settings, new(task, ApplicationExecutionEnvironment.Production()), new(run, ApplicationExecutionEnvironment.Production()), ApplicationExecutionEnvironment.Production());
            await service.SetEnabledAsync(true);
            AssertEx.True(task.Definition is not null && run.Value is null, "Successful migration left double registration.");
            run.Value = "legacy-command"; run.FailDelete = true; var taskBefore = task.Definition;
            await AssertEx.ThrowsAsync(() => service.SetEnabledAsync(true), "Run cleanup failure was ignored.");
            AssertEx.True(task.Definition == taskBefore && run.Value == "legacy-command", "Failed migration did not restore exact backend snapshots.");
            run.FailDelete = false; task.Unavailable = true;
            var fallbackSettingsPath = Path.Combine(root, "fallback.json"); using var fallbackSettings = new UserSettingsService(fallbackSettingsPath);
            var fallback = new WindowsStartupRegistrationService(fallbackSettings, new(task, ApplicationExecutionEnvironment.Production()), new(run, ApplicationExecutionEnvironment.Production()), ApplicationExecutionEnvironment.Production());
            await fallback.SetEnabledAsync(true);
            AssertEx.True((await fallback.GetSnapshotAsync()).OverallHealth == StartupRegistrationHealth.RegistryFallbackWithTaskStateUnknown,
                "Registry fallback concealed unknown Scheduler state.");
        }
        finally { Directory.Delete(root, true); }
    }
}
