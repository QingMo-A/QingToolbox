using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;
internal static class NotificationTests
{
    public static Task RunAsync()
    {
        var source = File.ReadAllText(Path.Combine(PipelineTests.FindRepo(), "QingToolbox.Shell", "Services", "NotificationAreaService.cs"));
        AssertEx.True(source.Contains("TaskbarCreated", StringComparison.Ordinal) && source.Contains("IsExiting", StringComparison.Ordinal), "Explorer recovery contract is missing.");
        AssertEx.True(source.Contains("DisposeResources();", StringComparison.Ordinal), "Explorer recovery does not replace the stale icon.");
        return Task.CompletedTask;
    }
}
