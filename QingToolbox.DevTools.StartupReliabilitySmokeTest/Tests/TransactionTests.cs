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
            await AssertEx.ThrowsAsync(() => fallback.SetEnabledAsync(true),
                "Unknown Scheduler state allowed an unsafe Registry fallback.");
            AssertEx.True(run.Value == "legacy-command" && !(await fallbackSettings.ReadAsync()).LaunchAtLogin,
                "Unknown Scheduler state changed Registry or settings.");

            var partialSettingsPath = Path.Combine(root, "partial.json");
            using var partialSettings = new UserSettingsService(partialSettingsPath);
            var partialTask = new FakeTaskStore { FailAfterRegister = true };
            var partialRun = new FakeRunStore();
            var partial = new WindowsStartupRegistrationService(partialSettings,
                new(partialTask, ApplicationExecutionEnvironment.Production()),
                new(partialRun, ApplicationExecutionEnvironment.Production()),
                ApplicationExecutionEnvironment.Production());
            await partial.SetEnabledAsync(true);
            AssertEx.True(partialTask.Definition is null && partialRun.Value is not null,
                "Registry fallback began before Scheduler partial success was exactly rolled back.");
            AssertEx.True((await partial.GetSnapshotAsync()).OverallHealth == StartupRegistrationHealth.HealthyRegistryFallback,
                "Safe Registry fallback was not reported after exact Scheduler rollback.");
        }
        finally { Directory.Delete(root, true); }
    }
}
