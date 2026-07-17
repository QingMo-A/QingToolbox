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

            var exactTask = new FakeTaskStore();
            var exactIdentity = OwnedStartupTaskIdentity.Create();
            var exactDefinition = new ScheduledStartupDefinition(exactIdentity.PreferredTaskPath,
                Environment.ProcessPath!, "--startup --startup-source TaskScheduler", AppContext.BaseDirectory,
                exactIdentity.CurrentUserSid, false, true, true, true, true, true, false, false, false,
                true, "PT0S", 0, "PT0S", TriggerCount: 3, ActionCount: 2);
            exactTask.Definition = exactDefinition;
            exactTask.PreferredXml = "<Task><Settings><Enabled>false</Enabled></Settings><Triggers><Custom/></Triggers><Actions><Extra/></Actions></Task>";
            var exactXml = exactTask.PreferredXml;
            var exactRun = new FakeRunStore { Value = "legacy", FailDelete = true };
            using var exactSettings = new UserSettingsService(Path.Combine(root, "exact.json"));
            var exactService = new WindowsStartupRegistrationService(exactSettings,
                new(exactTask, ApplicationExecutionEnvironment.Production()),
                new(exactRun, ApplicationExecutionEnvironment.Production()), ApplicationExecutionEnvironment.Production());
            await AssertEx.ThrowsAsync(() => exactService.SetEnabledAsync(true), "Exact rollback failure was hidden.");
            AssertEx.True(exactTask.Definition?.Enabled == false && exactTask.PreferredXml == exactXml,
                "Disabled task XML was not restored exactly.");

            var cancellationTask = new FakeTaskStore();
            using var caller = new CancellationTokenSource();
            cancellationTask.OnRegistered = caller.Cancel;
            using var cancellationSettings = new UserSettingsService(Path.Combine(root, "cancel.json"));
            var cancellationService = new WindowsStartupRegistrationService(cancellationSettings,
                new(cancellationTask, ApplicationExecutionEnvironment.Production()),
                new(new FakeRunStore(), ApplicationExecutionEnvironment.Production()), ApplicationExecutionEnvironment.Production());
            await AssertEx.ThrowsAsync(() => cancellationService.SetEnabledAsync(true, caller.Token),
                "Caller cancellation after task mutation did not propagate.");
            AssertEx.True(cancellationTask.TaskCount == 0 && !(await cancellationSettings.ReadAsync()).LaunchAtLogin,
                "Caller cancellation interrupted rollback after the commit point.");

            var timeoutTask = new FakeTaskStore { RestoreDelay = TimeSpan.FromMilliseconds(250) };
            timeoutTask.Definition = exactDefinition;
            var timeoutRun = new FakeRunStore { Value = "legacy", FailDelete = true };
            using var timeoutSettings = new UserSettingsService(Path.Combine(root, "rollback-timeout.json"));
            var timeoutService = new WindowsStartupRegistrationService(timeoutSettings,
                new(timeoutTask, ApplicationExecutionEnvironment.Production()),
                new(timeoutRun, ApplicationExecutionEnvironment.Production()), ApplicationExecutionEnvironment.Production(),
                rollbackBudget: TimeSpan.FromMilliseconds(40));
            Exception? timeoutFailure = null;
            try { await timeoutService.SetEnabledAsync(true); } catch (Exception exception) { timeoutFailure = exception; }
            AssertEx.True(timeoutFailure is StartupRegistrationTransactionException,
                "Bounded rollback timeout was not reported as a partial transaction failure.");

            var gateTask = new FakeTaskStore();
            var gateRun = new FakeRunStore();
            using var gateSettings = new UserSettingsService(Path.Combine(root, "gate.json"));
            var gateService = new WindowsStartupRegistrationService(gateSettings,
                new(gateTask, ApplicationExecutionEnvironment.Production()),
                new(gateRun, ApplicationExecutionEnvironment.Production()), ApplicationExecutionEnvironment.Production());
            await Task.WhenAll(gateService.SetEnabledAsync(true), gateService.RepairAsync());
            var reconcileSettings = await gateSettings.ReadAsync();
            await Task.WhenAll(gateService.SetEnabledAsync(false), gateService.ReconcileAsync(reconcileSettings));
            AssertEx.True(!(gateTask.Definition is not null && gateRun.Value is not null),
                "Concurrent startup mutations escaped the gate and left duplicate registrations.");
        }
        finally { Directory.Delete(root, true); }
    }
}
