namespace QingToolbox.Core.Updates;

public sealed record HostInstallationIdentity(bool IsSupported, string? InstallDirectory, string? InstalledVersion, string? Error = null);

public interface IHostInstallationIdentityService { HostInstallationIdentity Probe(); }
public sealed record HostInstallationRecord(string? InstallLocation, string? InstalledVersion, bool Exists);
public interface IHostInstallationRecordReader
{
    HostInstallationRecord ReadUninstallRecord();
    HostInstallationRecord ReadProductRecord();
}

public sealed class HostInstallationIdentityService(bool isProduction, string? executablePath, string currentVersion,
    IHostInstallationRecordReader records) : IHostInstallationIdentityService
{
    public HostInstallationIdentity Probe()
    {
        if (!isProduction) return Unsupported("One-click updates are available only in an installed Production build.");
        if (!TryNormalizePath(executablePath, false, out var executable)) return Unsupported("The current executable path is not a supported local installation.");
        var directory = Path.GetDirectoryName(executable);
        if (directory is null || IsRemote(directory) || !string.Equals(Path.GetFileName(executable), "QingToolbox.Shell.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(directory, "QingToolbox.Shell.exe"))) return Unsupported("The current executable is not in a supported installation directory.");
        HostInstallationRecord uninstall;
        HostInstallationRecord product;
        try { uninstall = records.ReadUninstallRecord(); product = records.ReadProductRecord(); }
        catch (Exception exception) when (IsExpectedIdentityException(exception))
        { return Unsupported("The installed QingToolbox registration could not be read safely."); }
        if (!uninstall.Exists || !product.Exists) return Unsupported("The installed QingToolbox registration is incomplete.");
        if (!TryDirectory(uninstall.InstallLocation, out var uninstallDirectory) || !TryDirectory(product.InstallLocation, out var productDirectory) ||
            !Same(uninstallDirectory!, productDirectory!) || !Same(directory, productDirectory!)) return Unsupported("The installed QingToolbox locations are missing or conflict.");
        if (!SemanticVersion.TryParse(product.InstalledVersion, out var installed) || !SemanticVersion.TryParse(currentVersion, out var current) ||
            installed!.CompareTo(current) != 0) return Unsupported("The installed QingToolbox version does not match the running version.");
        return new(true, directory, installed.ToString());
    }

    private static bool TryDirectory(string? value, out string? directory)
    {
        if (!TryNormalizePath(value, true, out directory)) return false;
        directory = directory!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return directory.Length > 0;
    }
    private static bool TryNormalizePath(string? value, bool directory, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            if (!Path.IsPathFullyQualified(value)) return false;
            var full = Path.GetFullPath(value);
            if (IsRemote(full) || (!directory && Path.GetFileName(full).Length == 0)) return false;
            normalized = full; return true;
        }
        catch (Exception exception) when (IsExpectedIdentityException(exception)) { return false; }
    }
    private static bool IsExpectedIdentityException(Exception exception) => exception is
        ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static bool IsRemote(string path) => path.StartsWith("\\\\", StringComparison.Ordinal);
    private static bool Same(string left, string right) => string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static HostInstallationIdentity Unsupported(string error) => new(false, null, null, error);
}

public sealed record HostInstallerLaunchRequest(VerifiedHostInstaller Installer, string WorkingDirectory,
    IReadOnlyList<string> Arguments, bool UseShellExecute);
public interface IHostInstallerLauncher { bool Start(HostInstallerLaunchRequest request); }

public enum HostUpdateHandoffState { Idle, Verifying, Starting, Started, Unsupported, InstallerInvalid, Failed }

public sealed record HostUpdateHandoffResult(HostUpdateHandoffState State, string? Error = null);

public sealed class HostUpdateHandoffCoordinator(
    HostUpdateInstallerDownloader downloader,
    IHostInstallationIdentityService installation,
    IHostInstallerLauncher launcher)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _started;
    public event EventHandler<HostUpdateHandoffState>? StateChanged;

    public HostInstallationIdentity ProbeInstallation() => installation.Probe();

    public async Task<HostUpdateHandoffResult> StartAsync(HostReleaseInfo release, CancellationToken token = default)
    {
        if (!await _gate.WaitAsync(0, token)) return new(HostUpdateHandoffState.Starting);
        try
        {
            if (_started) return new(HostUpdateHandoffState.Started);
            var identity = installation.Probe();
            if (!identity.IsSupported) return Change(new(HostUpdateHandoffState.Unsupported, identity.Error));
            Change(new(HostUpdateHandoffState.Verifying));
            var verified = await downloader.GetVerifiedInstallerForHandoffAsync(release, token);
            if (verified is null) return Change(new(HostUpdateHandoffState.InstallerInvalid, "The verified installer could not be confirmed."));
            Change(new(HostUpdateHandoffState.Starting));
            var request = new HostInstallerLaunchRequest(verified, Path.GetDirectoryName(verified.InstallerPath)!,
                ["/SILENT", "/NORESTART"], true);
            if (!launcher.Start(request)) return Change(new(HostUpdateHandoffState.Failed, "The installer process could not be started."));
            _started = true;
            return Change(new(HostUpdateHandoffState.Started));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Change(new(HostUpdateHandoffState.Failed, exception.Message));
        }
        finally { _gate.Release(); }
    }

    private HostUpdateHandoffResult Change(HostUpdateHandoffResult result)
    {
        StateChanged?.Invoke(this, result.State); return result;
    }
}
