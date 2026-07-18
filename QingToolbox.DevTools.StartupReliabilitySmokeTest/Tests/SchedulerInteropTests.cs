using System.Reflection;
using QingToolbox.Shell.Services;
using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;

internal static class SchedulerInteropTests
{
    public static Task RunAsync()
    {
        var missing = new FileNotFoundException("Task Scheduler reports a missing task.");
        var wrapped = new TargetInvocationException(missing);
        AssertEx.True(WindowsTaskSchedulerStore.IsMissing(wrapped),
            "A reflection-wrapped missing-task error must be treated as an absent task.");
        AssertEx.True(ReferenceEquals(
                WindowsTaskSchedulerStore.UnwrapInvocationException(wrapped), missing),
            "Task Scheduler reflection errors must preserve the original exception.");

        var snapshot = new WindowsTaskSchedulerStore().CaptureOwned();
        AssertEx.True(snapshot is not null,
            "Reading absent owned tasks must return a snapshot instead of terminating the process.");

        var identity = OwnedStartupTaskIdentity.Create();
        var store = new WindowsTaskSchedulerStore(identity);
        var testPath = identity.PreferredTestPath(Guid.NewGuid());
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The smoke test process path is unavailable.");
        var definition = new ScheduledStartupDefinition(
            testPath,
            executablePath,
            "--startup-reliability-smoke-test",
            Path.GetDirectoryName(executablePath)!,
            identity.CurrentUserSid,
            Enabled: false,
            LogonTrigger: true,
            InteractiveToken: true,
            LeastPrivilege: true,
            IgnoreNew: true,
            AllowOnBatteries: true,
            RunOnlyIfIdle: false,
            RunOnlyIfNetworkAvailable: false,
            WakeToRun: false,
            AllowStartOnDemand: true,
            ExecutionTimeLimit: "PT0S",
            RestartCount: 3,
            RestartInterval: "PT1M",
            TriggerUserId: identity.CurrentUserSid);
        try
        {
            store.RegisterAtPath(testPath, definition);
            var created = store.CaptureAtPath(testPath);
            AssertEx.True(created is not null,
                "A temporary current-user logon task must be registered and readable.");
        }
        finally
        {
            store.DeleteAtPath(testPath);
            store.DeleteOwnedStartupTests();
        }
        return Task.CompletedTask;
    }
}
