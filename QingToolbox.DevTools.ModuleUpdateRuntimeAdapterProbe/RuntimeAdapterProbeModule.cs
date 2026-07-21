using System.Reflection;
using System.IO;
using QingToolbox.Abstractions.Modules;

[assembly: AssemblyMetadata("QingToolboxCanaryVersion", "1.0.0")]

namespace QingToolbox.DevTools.ModuleUpdateRuntimeAdapterProbe;

public sealed class RuntimeAdapterProbeModule : IInProcessServiceModule
{
    private const string ProbeVersion = "1.0.0";
    private const string LifecycleFileName = "runtime-adapter-lifecycle.tsv";
    private ModuleContext? _context;
    private string? _lifecyclePath;

    public string Id => _context?.ModuleId ?? "qing.runtime-adapter-probe";

    public string Name => "Runtime Adapter Probe";

    public string Description => "A collectible UI-free lifecycle probe used only by the runtime adapter smoke test.";

    public Task OnLoadAsync(
        ModuleContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        _lifecyclePath = Path.Combine(context.DataDirectory, LifecycleFileName);
        AppendLifecycle("Load");
        return Task.CompletedTask;
    }

    public Task OnActivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendLifecycle("Activate");
        return Task.CompletedTask;
    }

    public Task OnDeactivateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendLifecycle("Deactivate");
        return Task.CompletedTask;
    }

    public Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AppendLifecycle("Unload");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        AppendLifecycle("Dispose");
        _context = null;
        _lifecyclePath = null;
        return ValueTask.CompletedTask;
    }

    private void AppendLifecycle(string operation)
    {
        var path = _lifecyclePath ?? throw new InvalidOperationException("Probe lifecycle path is unavailable.");
        File.AppendAllText(
            path,
            $"{operation}\t{ProbeVersion}\t{Environment.CurrentManagedThreadId}{Environment.NewLine}");
    }
}
