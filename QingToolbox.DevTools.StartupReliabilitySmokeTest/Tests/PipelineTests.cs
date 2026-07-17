using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;
internal static class PipelineTests
{
    public static Task RunAsync()
    {
        var root = FindRepo(); var app = File.ReadAllText(Path.Combine(root, "QingToolbox.Shell", "App.xaml.cs"));
        var window = File.ReadAllText(Path.Combine(root, "QingToolbox.Shell", "MainWindow.xaml.cs"));
        AssertEx.True(app.IndexOf("StartServer()", StringComparison.Ordinal) < app.IndexOf("BuildServiceProvider()", StringComparison.Ordinal), "Pipe server is not ready before DI.");
        AssertEx.True(window.IndexOf("PresentationReady", StringComparison.Ordinal) < window.IndexOf("InitializeDiscoveryAsync", StringComparison.Ordinal), "Discovery precedes PresentationReady.");
        AssertEx.True(window.Contains("_backgroundStartupTask =", StringComparison.Ordinal) && window.Contains("StopBackgroundStartupAsync", StringComparison.Ordinal), "Background startup task is not tracked.");
        return Task.CompletedTask;
    }
    internal static string FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QingToolbox.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
