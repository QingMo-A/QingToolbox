using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;
using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;

internal static class StartupTestTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "QingToolbox-startup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var journal = new StartupHealthJournal(Path.Combine(root, "startup-health.json"), TimeProvider.System);
            var store = new FakeTaskStore();
            store.OnRun = path =>
            {
                var name = OwnedStartupTaskIdentity.SplitTaskPath(path).Name;
                var prefix = name.StartsWith("QingToolbox-Test-", StringComparison.Ordinal)
                    ? "QingToolbox-Test-" : "Test-";
                var id = Guid.ParseExact(name[prefix.Length..], "D");
                journal.RecordStartupTestResult(id, StartupRegistrationTestStatus.AlreadyRunning);
            };
            var coordinator = new StartupTestCoordinator(store, journal, ApplicationExecutionEnvironment.Production());
            var result = await coordinator.RunAsync();
            AssertEx.True(result.Health == StartupRegistrationHealth.Healthy &&
                result.DiagnosticCode == "startup.testAlreadyRunning", "Correlated AlreadyRunning test did not succeed.");
            AssertEx.True(store.RunCount == 1 && store.FallbackDefinition is null,
                "Temporary startup task was not run exactly once and cleaned up.");

            var first = ApplicationLaunchOptions.Parse(["--startup", "--startup-source", "StartupTest",
                "--startup-test-id", Guid.NewGuid().ToString("D")]);
            AssertEx.True(first.StartupSource == StartupLaunchSource.StartupTest && first.StartupTestId is not null,
                "StartupTest arguments were not correlated.");

            var timeoutStore = new FakeTaskStore();
            var timeoutCoordinator = new StartupTestCoordinator(timeoutStore, journal,
                ApplicationExecutionEnvironment.Production(), TimeSpan.FromMilliseconds(100));
            var timeout = await timeoutCoordinator.RunAsync();
            AssertEx.True(timeout.DiagnosticCode == "startup.testTimedOut" && timeoutStore.FallbackDefinition is null,
                "A LastRun-only test did not time out or clean its task.");

            var cleanupStore = new FakeTaskStore { FailDelete = true };
            cleanupStore.OnRun = store.OnRun;
            var cleanupCoordinator = new StartupTestCoordinator(cleanupStore, journal,
                ApplicationExecutionEnvironment.Production(), TimeSpan.FromSeconds(1));
            var cleanup = await cleanupCoordinator.RunAsync();
            AssertEx.True(cleanup.DiagnosticCode == "startup.testCleanupFailed",
                "Temporary startup task cleanup failure was concealed.");

            var partialStore = new FakeTaskStore { FailAfterRegister = true };
            partialStore.OnRun = store.OnRun;
            var partial = await new StartupTestCoordinator(partialStore, journal,
                ApplicationExecutionEnvironment.Production(), TimeSpan.FromSeconds(1)).RunAsync();
            AssertEx.True(partial.DiagnosticCode == "startup.testAlreadyRunning" &&
                partialStore.RegisterCount == 1 && partialStore.TaskCount == 0,
                "Preferred partial success created a fallback or left a test task.");

            var failedStore = new FakeTaskStore();
            failedStore.OnRun = path =>
            {
                var name = OwnedStartupTaskIdentity.SplitTaskPath(path).Name;
                var prefix = name.StartsWith("QingToolbox-Test-", StringComparison.Ordinal) ? "QingToolbox-Test-" : "Test-";
                journal.RecordStartupTestResult(Guid.ParseExact(name[prefix.Length..], "D"), StartupRegistrationTestStatus.Failed);
            };
            var failed = await new StartupTestCoordinator(failedStore, journal,
                ApplicationExecutionEnvironment.Production(), TimeSpan.FromSeconds(1)).RunAsync();
            AssertEx.True(failed.DiagnosticCode == "startup.testFailed",
                "A failed startup test was incorrectly reported as timed out.");
        }
        finally { Directory.Delete(root, true); }
    }
}
