using QingToolbox.Shell.Startup;
using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;
internal static class JournalTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "QingToolbox-journal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero));
            var path = Path.Combine(root, "startup-health.json"); await using var journal = new StartupHealthJournal(path, clock);
            journal.SetSource(StartupLaunchSource.StartupTest);
            Parallel.ForEach(new[] { StartupPhase.InstanceReady, StartupPhase.MinimalServicesReady, StartupPhase.PresentationReady },
                phase => journal.Mark(phase));
            journal.Mark(StartupPhase.NotificationAreaReady, StartupPhaseOutcome.Degraded, "startup.notificationUnavailable");
            journal.Mark(StartupPhase.NotificationAreaReady, StartupPhaseOutcome.Succeeded, "startup.notificationRecovered");
            journal.Mark(StartupPhase.Ready); await journal.FlushAsync();
            var record = (await journal.ReadAsync()).Single();
            AssertEx.True(record.ReadyAt is not null && record.NotificationAreaReadyAt is not null, "Flush lost the final Ready snapshot.");
            AssertEx.True(record.PhaseOutcomes[nameof(StartupPhase.NotificationAreaReady)] == StartupPhaseOutcome.Succeeded, "Recovered notification phase remained degraded.");
            journal.Fail(StartupPhase.ModuleDiscoveryComplete, "startup.discoveryFailed", 12); await journal.FlushAsync();
            record = (await journal.ReadAsync()).Single();
            AssertEx.True(record.FailurePhase == StartupPhase.ModuleDiscoveryComplete, "Failure phase was replaced by Failed itself.");
            journal.RecordNotificationRecovery(); journal.Mark(StartupPhase.Exiting); await journal.FlushAsync();
            record = (await journal.ReadAsync()).Single(item => item.AttemptId == journal.Current.AttemptId);
            AssertEx.True(record.NotificationRecoveryCount == 1 && record.NotificationRecoveredAt == clock.Now && record.ExitingAt == clock.Now,
                "Explorer recovery or final Exiting state was not durably journaled with TimeProvider.");

            var stale = Path.Combine(root, "startup-health.json.tmp.stale");
            await File.WriteAllTextAsync(stale, "stale");
            File.SetLastWriteTimeUtc(stale, clock.Now.UtcDateTime.AddDays(-2));
            journal.Mark(StartupPhase.Ready); await journal.FlushAsync();
            AssertEx.True(!File.Exists(stale), "Temporary cleanup ignored the injected TimeProvider.");

            await using var concurrent = new StartupHealthJournal(path, clock);
            var testId = Guid.NewGuid();
            concurrent.RecordStartupTestResult(testId, StartupRegistrationTestStatus.AlreadyRunning);
            journal.RecordStartupTestResult(Guid.NewGuid(), StartupRegistrationTestStatus.PresentationReady);
            await Task.WhenAll(concurrent.FlushAsync(), journal.FlushAsync());
            AssertEx.True((await journal.ReadAsync()).Any(item => item.StartupTestId == testId),
                "Concurrent journal read/persist lost the correlated startup-test record.");
        }
        finally { Directory.Delete(root, true); }
    }
}
