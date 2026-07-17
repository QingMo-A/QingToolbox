using StartupReliabilitySmokeTest.Fakes;

namespace StartupReliabilitySmokeTest.Tests;
internal static class InstallerContractTests
{
    public static Task RunAsync()
    {
        var root = PipelineTests.FindRepo(); var installer = File.ReadAllText(Path.Combine(root, "installer", "QingToolbox.iss"));
        var publish = File.ReadAllText(Path.Combine(root, "scripts", "build-installer.ps1"));
        AssertEx.True(installer.Contains("[UninstallRun]", StringComparison.Ordinal) && installer.Contains("--remove-owned-startup", StringComparison.Ordinal), "Uninstaller does not invoke owned startup cleanup.");
        AssertEx.True(publish.Contains("QingToolbox.StartupMaintenance.exe", StringComparison.Ordinal), "Maintenance tool is absent from installer payload audit.");
        AssertEx.True(!installer.Contains("schtasks.exe", StringComparison.OrdinalIgnoreCase), "Installer uses schtasks.exe.");
        return Task.CompletedTask;
    }
}
