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
            var path = Path.Combine(root, "startup-health.json"); await using var journal = new StartupHealthJournal(path, TimeProvider.System);
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
        }
        finally { Directory.Delete(root, true); }
    }
}
