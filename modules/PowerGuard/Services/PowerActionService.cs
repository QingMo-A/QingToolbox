using System.Diagnostics;
using System.IO;

namespace QingToolbox.Modules.PowerGuard.Services;

public interface IPowerActionService { Task<bool> RequestNormalShutdownAsync(CancellationToken token = default); }

public sealed class PowerActionService : IPowerActionService
{
    private int _requested;
    public Task<bool> RequestNormalShutdownAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _requested, 1) != 0) return Task.FromResult(false);
        try
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            Process.Start(new ProcessStartInfo(executable, "/s /t 0") { UseShellExecute = false, CreateNoWindow = true });
            return Task.FromResult(true);
        }
        catch { return Task.FromResult(false); }
    }
}
