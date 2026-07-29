using System.Security.Cryptography;
using System.Text;
using QingToolbox.Core.Updates;

await Smoke.RunAsync();

static class Smoke
{
    public static async Task RunAsync()
    {
        await InTemp(async root => { TestIdentity(root); await TestHandoffAsync(root); });
        Console.WriteLine("Host update handoff smoke test passed: installed identity, final revalidation, exact setup arguments, failure and duplicate-start gates.");
    }

    private static void TestIdentity(string root)
    {
        var exe = Path.Combine(root, "QingToolbox.Shell.exe"); File.WriteAllText(exe, "host");
        var valid = new Records(new(root, null, true), new(root, "0.2.0-alpha", true));
        Assert(new HostInstallationIdentityService(true, exe, "0.2.0-alpha", valid).Probe().IsSupported, "matching installed identity");
        Assert(!new HostInstallationIdentityService(false, exe, "0.2.0-alpha", valid).Probe().IsSupported, "Development rejected");
        Assert(!new HostInstallationIdentityService(true, exe, "0.2.0-alpha", new Records(new(null, null, false), valid.Product)).Probe().IsSupported, "uninstall missing");
        Assert(!new HostInstallationIdentityService(true, exe, "0.2.0-alpha", new Records(valid.Uninstall, new(null, null, false))).Probe().IsSupported, "product missing");
        Assert(!new HostInstallationIdentityService(true, exe, "0.2.0-alpha", new Records(valid.Uninstall, new(Path.Combine(root, "other"), "0.2.0-alpha", true))).Probe().IsSupported, "locations conflict");
        Assert(!new HostInstallationIdentityService(true, exe, "0.2.0-alpha", new Records(valid.Uninstall, new(root, "invalid", true))).Probe().IsSupported, "invalid version");
        Assert(!new HostInstallationIdentityService(true, exe, "0.2.0-alpha", new Records(valid.Uninstall, new(root, "0.3.0-alpha", true))).Probe().IsSupported, "version mismatch");
        Assert(!new HostInstallationIdentityService(true, Path.Combine(root, "copy.exe"), "0.2.0-alpha", valid).Probe().IsSupported, "copied executable rejected");
        Assert(!new HostInstallationIdentityService(true, root + "\\invalid\0shell.exe", "0.2.0-alpha", valid).Probe().IsSupported, "malformed executable path rejected");
        Assert(!new HostInstallationIdentityService(true, "QingToolbox.Shell.exe", "0.2.0-alpha", valid).Probe().IsSupported, "relative executable path rejected");
        Assert(!new HostInstallationIdentityService(true, "\\\\server\\share\\QingToolbox.Shell.exe", "0.2.0-alpha", valid).Probe().IsSupported, "remote executable path rejected");
        Assert(!new HostInstallationIdentityService(true, exe, "0.2.0-alpha", new Records(new(root + "\\invalid\0location", null, true), valid.Product)).Probe().IsSupported, "malformed uninstall location rejected");
        Assert(!new HostInstallationIdentityService(true, exe, "0.2.0-alpha", new Records(valid.Uninstall, new(root + "\\invalid\0location", "0.2.0-alpha", true))).Probe().IsSupported, "malformed product location rejected");
        Assert(!new HostInstallationIdentityService(true, exe, "0.2.0-alpha", new ThrowingRecords()).Probe().IsSupported, "registry read failure rejected");
        File.Delete(exe);
        Assert(!new HostInstallationIdentityService(true, exe, "0.2.0-alpha", valid).Probe().IsSupported, "missing shell rejected");
    }

    private static async Task TestHandoffAsync(string root)
    {
        var cache = Path.Combine(root, "cache"); var release = Release(); var payload = Encoding.UTF8.GetBytes("verified setup");
        var sidecarText = $"{Convert.ToHexString(SHA256.HashData(payload))}  {release.Installer.Name}\n";
        release = release with { Installer = release.Installer with { Size = payload.Length }, Checksum = release.Checksum with { Size = Encoding.UTF8.GetByteCount(sidecarText) } };
        var directory = Path.Combine(cache, release.Version, $"{release.Installer.Id}-{release.Checksum.Id}"); Directory.CreateDirectory(directory);
        var installer = Path.Combine(directory, release.Installer.Name); var sidecar = Path.Combine(directory, release.Checksum.Name);
        await File.WriteAllBytesAsync(installer, payload);
        await File.WriteAllTextAsync(sidecar, sidecarText);
        var downloader = new HostUpdateInstallerDownloader(new HttpClient(new NeverHandler()), cache);
        var verified = await downloader.GetVerifiedInstallerForHandoffAsync(release, CancellationToken.None);
        Assert(verified is not null && verified.InstallerAssetId == 11 && verified.ChecksumAssetId == 12, "final verification");
        var replacedAsset = release with { Installer = release.Installer with { Id = 99 } };
        Assert(await downloader.GetVerifiedInstallerForHandoffAsync(replacedAsset, CancellationToken.None) is null, "changed asset identity rejects old cache");
        await File.WriteAllTextAsync(sidecar, $"{new string('0', 64)}  {release.Installer.Name}\n");
        Assert(await downloader.GetVerifiedInstallerForHandoffAsync(release, CancellationToken.None) is null, "modified sidecar rejected");
        await File.WriteAllTextAsync(sidecar, sidecarText); File.Delete(sidecar);
        Assert(await downloader.GetVerifiedInstallerForHandoffAsync(release, CancellationToken.None) is null, "missing sidecar rejected");
        await File.WriteAllTextAsync(sidecar, sidecarText);

        var launcher = new Launcher(true); var coordinator = new HostUpdateHandoffCoordinator(downloader, new Identity(true), launcher);
        var started = await coordinator.StartAsync(release);
        Assert(started.State == HostUpdateHandoffState.Started && launcher.Calls == 1, "handoff started once");
        Assert(launcher.Request!.UseShellExecute && launcher.Request.WorkingDirectory == directory, "controlled launch path");
        Assert(launcher.Request.Arguments.SequenceEqual(new[] { "/SILENT", "/NORESTART" }) && !launcher.Request.Arguments.Any(x => x.StartsWith("/DIR") || x == "/VERYSILENT"), "exact safe arguments");
        await coordinator.StartAsync(release); Assert(launcher.Calls == 1, "duplicate start blocked");

        await File.WriteAllTextAsync(installer, "tampered");
        var blockedLauncher = new Launcher(true); var invalid = await new HostUpdateHandoffCoordinator(downloader, new Identity(true), blockedLauncher).StartAsync(release);
        Assert(invalid.State == HostUpdateHandoffState.InstallerInvalid && blockedLauncher.Calls == 0, "tampered installer blocked");
        Assert((await new HostUpdateHandoffCoordinator(downloader, new Identity(false), blockedLauncher).StartAsync(release)).State == HostUpdateHandoffState.Unsupported, "unsupported deployment blocked");

        await File.WriteAllBytesAsync(installer, payload); var failedLauncher = new Launcher(false);
        var failedCoordinator = new HostUpdateHandoffCoordinator(downloader, new Identity(true), failedLauncher);
        Assert((await failedCoordinator.StartAsync(release)).State == HostUpdateHandoffState.Failed, "null or failed process retained app state");
        Assert((await failedCoordinator.StartAsync(release)).State == HostUpdateHandoffState.Failed && failedLauncher.Calls == 2, "launcher failure permits retry");
        var throwing = new HostUpdateHandoffCoordinator(downloader, new Identity(true), new ThrowingLauncher());
        Assert((await throwing.StartAsync(release)).State == HostUpdateHandoffState.Failed, "launcher exception preserves running application");
        var escaped = release with { Installer = release.Installer with { Name = "..\\escape.exe" } };
        Assert((await new HostUpdateHandoffCoordinator(downloader, new Identity(true), blockedLauncher).StartAsync(escaped)).State == HostUpdateHandoffState.Failed, "path escape rejected");
    }

    private static HostReleaseInfo Release()
    {
        const string version = "0.3.0-alpha"; var name = $"QingToolbox-{version}-win-x64-setup.exe";
        return new(version, DateTimeOffset.UtcNow, "notes", new(11, name, new("https://github.com/file"), 1), new(12, name + ".sha256", new("https://github.com/file.sha256"), 100));
    }
    private static async Task InTemp(Func<string, Task> action) { var root = Path.Combine(Path.GetTempPath(), "QingToolbox-handoff-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { await action(root); } finally { Directory.Delete(root, true); } }
    private static void Assert(bool value, string name) { if (!value) throw new InvalidOperationException("Failed: " + name); }
    private sealed record Records(HostInstallationRecord Uninstall, HostInstallationRecord Product) : IHostInstallationRecordReader { public HostInstallationRecord ReadUninstallRecord() => Uninstall; public HostInstallationRecord ReadProductRecord() => Product; }
    private sealed class ThrowingRecords : IHostInstallationRecordReader { public HostInstallationRecord ReadUninstallRecord() => throw new UnauthorizedAccessException("denied"); public HostInstallationRecord ReadProductRecord() => throw new InvalidOperationException("unreachable"); }
    private sealed class Identity(bool supported) : IHostInstallationIdentityService { public HostInstallationIdentity Probe() => new(supported, supported ? "C:\\App" : null, supported ? "0.2.0-alpha" : null); }
    private sealed class Launcher(bool result) : IHostInstallerLauncher { public int Calls; public HostInstallerLaunchRequest? Request; public bool Start(HostInstallerLaunchRequest request) { Calls++; Request = request; return result; } }
    private sealed class ThrowingLauncher : IHostInstallerLauncher { public bool Start(HostInstallerLaunchRequest request) => throw new InvalidOperationException("launch failed"); }
    private sealed class NeverHandler : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Network must not be used during handoff."); }
}
