using System.Text.Json;

namespace QingToolbox.Shell.WebShell;

public interface IWebCommandHandler
{
    string Command { get; }
    Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken);
}

public sealed class WebPingCommandHandler(TimeProvider timeProvider) : IWebCommandHandler
{
    public string Command => "app.ping";
    public Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
        Task.FromResult<object>(new WebPingResponse(true, timeProvider.GetUtcNow()));
}

public sealed class WebSnapshotCommandHandler(WebAppSnapshotProvider snapshots) : IWebCommandHandler
{
    public string Command => "app.getSnapshot";
    public Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
        Task.FromResult<object>(snapshots.Create());
}

public sealed class WebReadyCommandHandler(WebAppSnapshotProvider snapshots) : IWebCommandHandler
{
    public string Command => "web.ready";
    public Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken) =>
        Task.FromResult<object>(snapshots.Create());
}
