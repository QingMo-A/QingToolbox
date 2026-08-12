using System.Text.Json;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.DevTools.WebModuleCanary;

public sealed class CanaryModule : IWebToolModule
{
    private ModuleContext? _context;
    private string? _dataDirectory;
    private bool _active;

    public string Id => "qing.web.canary";
    public string Name => "Web Module Canary";
    public string Description => "A small Web backend/bridge lifecycle canary.";
    public event EventHandler<ModuleWebEventArgs>? WebEvent;

    public Task OnLoadAsync(ModuleContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        _dataDirectory = context.DataDirectory;
        Record("load");
        return Task.CompletedTask;
    }

    public Task OnActivateAsync(CancellationToken cancellationToken = default)
    {
        _active = true;
        Record("activate");
        WebEvent?.Invoke(this, new ModuleWebEventArgs("stateChanged", StatePayload()));
        return Task.CompletedTask;
    }

    public Task OnDeactivateAsync(CancellationToken cancellationToken = default)
    {
        _active = false;
        Record("deactivate");
        return Task.CompletedTask;
    }

    public Task<JsonElement?> HandleWebRequestAsync(string method, JsonElement? payload,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return method switch
        {
            "getState" => Task.FromResult<JsonElement?>(StatePayload()),
            "echo" => Task.FromResult(payload),
            _ => throw new InvalidOperationException("Unknown canary method.")
        };
    }

    public Task OnUnloadAsync(CancellationToken cancellationToken = default)
    {
        Record("unload");
        _context = null;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Record("dispose");
        _context = null;
        return ValueTask.CompletedTask;
    }

    private JsonElement StatePayload() => JsonSerializer.SerializeToElement(new { loaded = _context is not null, active = _active });

    private void Record(string stage)
    {
        if (_dataDirectory is null) return;
        Directory.CreateDirectory(_dataDirectory);
        File.AppendAllText(Path.Combine(_dataDirectory, "lifecycle.log"), stage + Environment.NewLine);
    }
}
