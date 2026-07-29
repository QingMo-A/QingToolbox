using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using QingToolbox.Core.Updates;

namespace QingToolbox.Shell.Services;

public sealed class WindowsHostInstallationRecordReader : IHostInstallationRecordReader
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{9F2E7B13-3A62-4F66-B88C-5B6DBD8AE7C4}_is1";
    private const string ProductKey = @"Software\QingMo-A\QingToolbox";
    public HostInstallationRecord ReadUninstallRecord() => Read(UninstallKey);
    public HostInstallationRecord ReadProductRecord() => Read(ProductKey);
    private static HostInstallationRecord Read(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, false);
            return key is null ? new(null, null, false) : new(key.GetValue("InstallLocation") as string,
                key.GetValue("InstalledVersion") as string ?? key.GetValue("DisplayVersion") as string, true);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return new(null, null, false); }
    }
}

public sealed class HostInstallerLauncher : IHostInstallerLauncher
{
    public bool Start(HostInstallerLaunchRequest request)
    {
        try
        {
            var start = new ProcessStartInfo(request.Installer.InstallerPath)
            {
                UseShellExecute = request.UseShellExecute,
                WorkingDirectory = request.WorkingDirectory
            };
            foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
            return Process.Start(start) is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException) { return false; }
    }
}
