using System.Text.Json;

namespace QingToolbox.Abstractions.Modules;

/// <summary>Backend contract for a Web UI module. Requests remain module-owned and explicit.</summary>
public interface IWebToolModule : IModuleLifecycle
{
    event EventHandler<ModuleWebEventArgs>? WebEvent;
    Task<JsonElement?> HandleWebRequestAsync(string method, JsonElement? payload,
        CancellationToken cancellationToken = default);
}
