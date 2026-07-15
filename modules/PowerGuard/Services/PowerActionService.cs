using System.Diagnostics;
using System.IO;

namespace QingToolbox.Modules.PowerGuard.Services;

public enum PowerActionResult { Started, AlreadyRequested, Failed }
public interface IPowerActionService { Task<PowerActionResult> RequestNormalShutdownAsync(CancellationToken token = default); }
public interface IProcessLauncher { Process? Start(ProcessStartInfo startInfo); }
internal sealed class SystemProcessLauncher : IProcessLauncher { public Process? Start(ProcessStartInfo startInfo)=>Process.Start(startInfo); }

public sealed class PowerActionService(IProcessLauncher? launcher = null) : IPowerActionService
{
    private int _requested;
    private readonly IProcessLauncher _launcher=launcher??new SystemProcessLauncher();
    public Task<PowerActionResult> RequestNormalShutdownAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _requested,1,0)!=0) return Task.FromResult(PowerActionResult.AlreadyRequested);
        try
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            if(!File.Exists(executable)||_launcher.Start(new ProcessStartInfo(executable, "/s /t 0") { UseShellExecute = false, CreateNoWindow = true }) is null)
            { Interlocked.Exchange(ref _requested,0); return Task.FromResult(PowerActionResult.Failed); }
            return Task.FromResult(PowerActionResult.Started);
        }
        catch { Interlocked.Exchange(ref _requested,0); return Task.FromResult(PowerActionResult.Failed); }
    }
}
