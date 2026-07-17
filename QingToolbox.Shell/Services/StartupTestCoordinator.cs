using System.Runtime.InteropServices;
using System.IO;
using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.Services;

public sealed class StartupTestCoordinator(
    ITaskSchedulerStore store,
    StartupHealthJournal journal,
    ApplicationExecutionEnvironment environment,
    TimeSpan? testTimeout = null) : IStartupTestCoordinator
{
    private static string ExecutablePath => Path.GetFullPath(Environment.ProcessPath ??
        throw new InvalidOperationException("Executable path unavailable."));

    public async Task<StartupRegistrationTestResult> RunAsync(CancellationToken token = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        if (!environment.AllowWindowsStartupRegistration)
            return Result(Guid.Empty, StartupRegistrationTestOutcome.Failed, "startup.environmentDisabled", startedAt, null, true);

        var identity = OwnedStartupTaskIdentity.Create();
        var testId = Guid.NewGuid();
        var preferredPath = identity.PreferredTestPath(testId);
        var fallbackPath = identity.FallbackTestPath(testId);
        var path = preferredPath;
        var definition = new ScheduledStartupDefinition(
            path, ExecutablePath,
            $"--startup --startup-source StartupTest --startup-test-id {testId:D}",
            Path.GetDirectoryName(ExecutablePath)!, identity.CurrentUserSid,
            true, false, true, true, true, true, false, false, false,
            true, "PT2M", 0, "PT0S", TaskName: OwnedStartupTaskIdentity.SplitTaskPath(path).Name,
            TriggerCount: 0, TriggerEnabled: false, TriggerUserId: string.Empty);
        StartupRegistrationTestResult result = Result(testId, StartupRegistrationTestOutcome.Failed, "startup.testFailed", startedAt, path, true);
        var cleanupFailed = false;
        var cleanupEligible = false;
        try
        {
            var preferredBefore = await Task.Run(() => store.CaptureAtPath(preferredPath), token);
            var fallbackBefore = await Task.Run(() => store.CaptureAtPath(fallbackPath), token);
            if (preferredBefore is not null || fallbackBefore is not null)
                return Result(testId, StartupRegistrationTestOutcome.Failed, "startup.testConflict", startedAt, null, true);
            cleanupEligible = true;

            try { await Task.Run(() => store.RegisterAtPath(path, definition), token); }
            catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or IOException)
            {
                var partial = await Task.Run(() => store.CaptureAtPath(preferredPath), token);
                if (partial is not null)
                {
                    if (!Matches(partial, definition))
                        return Result(testId, StartupRegistrationTestOutcome.Failed, "startup.testConflict", startedAt, path, true);
                }
                else
                {
                    path = fallbackPath;
                    definition = definition with { TaskPath = path, TaskName = OwnedStartupTaskIdentity.SplitTaskPath(path).Name };
                    await Task.Run(() => store.RegisterAtPath(path, definition), token);
                }
            }
            journal.RecordStartupTestResult(testId, StartupRegistrationTestStatus.Started);
            await journal.FlushAsync(token);
            await Task.Run(() => store.RunAtPath(path), token);
            var journalResult = await journal.WaitForStartupTestAsync(testId,
                testTimeout ?? TimeSpan.FromMinutes(2), token);
            if (journalResult?.StartupTestResult == StartupRegistrationTestStatus.PresentationReady)
                result = Result(testId, StartupRegistrationTestOutcome.PresentationReady, "startup.testPresentationReady", startedAt, path, true);
            else if (journalResult?.StartupTestResult == StartupRegistrationTestStatus.AlreadyRunning)
                result = Result(testId, StartupRegistrationTestOutcome.AlreadyRunning, "startup.testAlreadyRunning", startedAt, path, true);
            else if (journalResult?.StartupTestResult == StartupRegistrationTestStatus.Failed ||
                     journalResult?.StartupTestExecutionResult == StartupRegistrationTestStatus.Failed)
            {
                result = Result(testId, StartupRegistrationTestOutcome.Failed, "startup.testFailed", startedAt, path, true);
            }
            else
            {
                journal.RecordStartupTestResult(testId, StartupRegistrationTestStatus.TimedOut);
                result = Result(testId, StartupRegistrationTestOutcome.TimedOut, "startup.testTimedOut", startedAt, path, true);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or IOException)
        {
            journal.RecordStartupTestResult(testId, StartupRegistrationTestStatus.Failed);
            result = Result(testId, StartupRegistrationTestOutcome.Failed, "startup.testFailed", startedAt, path, true);
        }
        finally
        {
            if (cleanupEligible)
            {
                foreach (var candidate in new[] { preferredPath, fallbackPath })
                {
                    try
                    {
                        var snapshot = await Task.Run(() => store.CaptureAtPath(candidate));
                        if (snapshot is not null && ContainsTestId(snapshot, testId))
                            await Task.Run(() => store.DeleteAtPath(candidate));
                        if (await Task.Run(() => store.CaptureAtPath(candidate)) is not null)
                            cleanupFailed = true;
                    }
                    catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or IOException)
                    { cleanupFailed = true; }
                }
                if (cleanupFailed)
                {
                    journal.RecordStartupTestResult(testId, StartupRegistrationTestStatus.CleanupFailed);
                    result = result with { DiagnosticCode = "startup.testCleanupFailed", CleanupSucceeded = false,
                        CompletedAt = DateTimeOffset.UtcNow };
                }
            }
        }
        return result;
    }

    private static bool Matches(OwnedTaskDefinitionSnapshot snapshot, ScheduledStartupDefinition definition) =>
        string.Equals(snapshot.TaskPath, definition.TaskPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(snapshot.HealthDefinition.ExecutablePath, definition.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(snapshot.HealthDefinition.Arguments, definition.Arguments, StringComparison.Ordinal);

    private static bool ContainsTestId(OwnedTaskDefinitionSnapshot snapshot, Guid testId) =>
        snapshot.HealthDefinition.Arguments.Contains(testId.ToString("D"), StringComparison.OrdinalIgnoreCase);

    private static StartupRegistrationTestResult Result(Guid testId, StartupRegistrationTestOutcome status,
        string code, DateTimeOffset startedAt, string? path, bool cleanupSucceeded) =>
        new(testId, status, code, startedAt, DateTimeOffset.UtcNow, path, cleanupSucceeded);
}
