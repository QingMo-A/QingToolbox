using QingToolbox.Core.Settings;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;
using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;
internal static class ReconcileTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "QingToolbox-reconcile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var settingsService = new UserSettingsService(Path.Combine(root, "settings.json"));
            var task = new FakeTaskStore(); var run = new FakeRunStore();
            var service = new WindowsStartupRegistrationService(settingsService, new(task, ApplicationExecutionEnvironment.Production()), new(run, ApplicationExecutionEnvironment.Production()), ApplicationExecutionEnvironment.Production());
            await service.SetEnabledAsync(true); var registrations = task.RegisterCount;
            task.Definition = task.Definition! with { Enabled = false };
            await service.ReconcileAsync(await settingsService.ReadAsync());
            AssertEx.True(task.RegisterCount == registrations && task.Definition.Enabled == false, "Reconcile re-enabled externally disabled task.");
            await settingsService.UpdateAsync(settings => { settings.LaunchAtLogin = false; settings.StartupRegistrationCleanupPending = true; });
            task.Unavailable = false; await service.ReconcileAsync(await settingsService.ReadAsync());
            AssertEx.True(task.Definition is null && run.Value is null && !(await settingsService.ReadAsync()).StartupRegistrationCleanupPending,
                "CleanupPending was not reconciled.");
        }
        finally { Directory.Delete(root, true); }
    }
}
