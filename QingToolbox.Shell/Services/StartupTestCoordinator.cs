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

    public async Task<StartupRegistrationState> RunAsync(CancellationToken token = default)
    {
        if (!environment.AllowWindowsStartupRegistration)
            return Result(StartupRegistrationHealth.SchedulerUnavailable, "startup.environmentDisabled");

        var identity = OwnedStartupTaskIdentity.Create();
        var testId = Guid.NewGuid();
        var path = identity.PreferredTestPath(testId);
        var definition = new ScheduledStartupDefinition(
            path, ExecutablePath,
            $"--startup --startup-source StartupTest --startup-test-id {testId:D}",
            Path.GetDirectoryName(ExecutablePath)!, identity.CurrentUserSid,
            true, false, true, true, true, true, false, false, false,
            true, "PT2M", 0, "PT0S", TaskName: OwnedStartupTaskIdentity.SplitTaskPath(path).Name,
            TriggerCount: 0, TriggerEnabled: false, TriggerUserId: string.Empty);
        StartupRegistrationState result;
        try
        {
            try { await Task.Run(() => store.RegisterAtPath(path, definition), token); }
            catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or IOException)
            {
                path = identity.FallbackTestPath(testId);
                definition = definition with { TaskPath = path, TaskName = OwnedStartupTaskIdentity.SplitTaskPath(path).Name };
                await Task.Run(() => store.RegisterAtPath(path, definition), token);
            }
            journal.RecordStartupTestResult(testId, StartupRegistrationTestStatus.Started);
            await journal.FlushAsync(token);
            await Task.Run(() => store.RunAtPath(path), token);
            var journalResult = await journal.WaitForStartupTestAsync(testId,
                testTimeout ?? TimeSpan.FromMinutes(2), token);
            if (journalResult?.StartupTestResult == StartupRegistrationTestStatus.PresentationReady)
                result = Result(StartupRegistrationHealth.Healthy, "startup.testPresentationReady");
            else if (journalResult?.StartupTestResult == StartupRegistrationTestStatus.AlreadyRunning)
                result = Result(StartupRegistrationHealth.Healthy, "startup.testAlreadyRunning");
            else
            {
                journal.RecordStartupTestResult(testId, StartupRegistrationTestStatus.TimedOut);
                result = Result(StartupRegistrationHealth.PartialFailure, "startup.testTimedOut");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or IOException)
        {
            journal.RecordStartupTestResult(testId, StartupRegistrationTestStatus.Failed);
            result = Result(StartupRegistrationHealth.PartialFailure, "startup.testFailed");
        }
        finally
        {
            try { await Task.Run(() => store.DeleteAtPath(path), CancellationToken.None); }
            catch
            {
                journal.RecordStartupTestResult(testId, StartupRegistrationTestStatus.CleanupFailed);
                result = Result(StartupRegistrationHealth.PartialFailure, "startup.testCleanupFailed");
            }
        }
        return result;
    }

    private static StartupRegistrationState Result(StartupRegistrationHealth health, string code) =>
        new(true, StartupRegistrationBackendKind.TaskScheduler, health,
            DiagnosticCode: code, Presence: StartupRegistrationPresence.Present);
}
