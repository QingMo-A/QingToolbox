using StartupReliabilitySmokeTest.Fakes;
using QingToolbox.Shell.Startup;

namespace StartupReliabilitySmokeTest.Tests;
internal static class PipelineTests
{
    public static async Task RunAsync()
    {
        var order = new List<string>();
        var coordinator = new StartupPipelineCoordinator();
        var uiThread = Environment.CurrentManagedThreadId;
        var discoveryThread = uiThread;
        var results = await coordinator.RunAsync(
        [
            new("Registration", _ => { order.Add("registration"); throw new IOException("degraded"); }),
            new("Discovery", async ct =>
            {
                await Task.Run(() => { discoveryThread = Environment.CurrentManagedThreadId; order.Add("discovery"); }, ct);
            }),
            new("Restore", _ => { order.Add("restore"); return Task.CompletedTask; }),
            new("Update", _ => { order.Add("update"); throw new HttpRequestException("offline"); })
        ]);
        AssertEx.True(order.SequenceEqual(["registration", "discovery", "restore", "update"]),
            "Executable startup pipeline order changed.");
        AssertEx.True(results[0].Outcome == StartupPhaseOutcome.Degraded &&
            results[1].Outcome == StartupPhaseOutcome.Succeeded &&
            results[2].Outcome == StartupPhaseOutcome.Succeeded &&
            results[3].Outcome == StartupPhaseOutcome.Degraded,
            "Auxiliary failure stopped or misreported the executable startup pipeline.");
        AssertEx.True(discoveryThread != uiThread, "Discovery work did not leave the captured UI thread.");

        // Static checks remain supplemental guards for the composition root only.
        var root = FindRepo(); var app = File.ReadAllText(Path.Combine(root, "QingToolbox.Shell", "App.xaml.cs"));
        var window = File.ReadAllText(Path.Combine(root, "QingToolbox.Shell", "MainWindow.xaml.cs"));
        AssertEx.True(app.IndexOf("StartServer()", StringComparison.Ordinal) < app.IndexOf("EnsureDirectories()", StringComparison.Ordinal), "Pipe server is not ready before directories/settings.");
        AssertEx.True(window.IndexOf("PresentationReady", StringComparison.Ordinal) < window.IndexOf("InitializeDiscoveryAsync", StringComparison.Ordinal), "Discovery precedes PresentationReady.");
        AssertEx.True(window.Contains("_backgroundStartupTask =", StringComparison.Ordinal) && window.Contains("StopBackgroundStartupAsync", StringComparison.Ordinal), "Background startup task is not tracked.");
        var xaml = File.ReadAllText(Path.Combine(root, "QingToolbox.Shell", "MainWindow.xaml"));
        AssertEx.True(xaml.Contains("HasIcon, Converter={StaticResource BooleanToVisibilityConverter}", StringComparison.Ordinal) &&
            xaml.Contains("<WrapPanel", StringComparison.Ordinal) && xaml.Contains("Margin=\"0,0,8,8\"", StringComparison.Ordinal),
            "User module-card icon or responsive action layout regressed.");
        var launcher = File.ReadAllText(Path.Combine(root, "run-latest.bat"));
        foreach (var argument in new[] { "--environment Development", "--profile Shell", "--repo-root" })
            AssertEx.True(launcher.Contains(argument, StringComparison.OrdinalIgnoreCase),
                $"Development launcher lost {argument}.");
    }
    internal static string FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QingToolbox.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
