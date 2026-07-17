using QingToolbox.Shell.Services;
using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;
internal static class TaskPathTests
{
    public static Task RunAsync()
    {
        var identity = OwnedStartupTaskIdentity.Create("S-1-5-21-1-2-3-1001");
        var preferred = OwnedStartupTaskIdentity.SplitTaskPath(identity.PreferredTaskPath);
        var fallback = OwnedStartupTaskIdentity.SplitTaskPath(identity.FallbackTaskPath);
        AssertEx.True(preferred.Folder == "\\QingToolbox" && preferred.Name == identity.PreferredTaskName, "Preferred task split failed.");
        AssertEx.True(fallback.Folder == "\\" && fallback.Name == identity.FallbackTaskName, "Root fallback split failed.");
        foreach (var invalid in new[] { "", "Task", "\\", "\\Task\\", "\\..", "\\Folder\\..", "\\Bad\u0001Task" })
        { try { OwnedStartupTaskIdentity.SplitTaskPath(invalid); throw new InvalidOperationException($"Invalid task path accepted: {invalid}"); } catch (ArgumentException) { } }
        return Task.CompletedTask;
    }
}
