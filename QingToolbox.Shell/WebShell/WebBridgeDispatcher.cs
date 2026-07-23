using System.Text.Json;

namespace QingToolbox.Shell.WebShell;

public sealed class WebBridgeDispatcher(IEnumerable<IWebCommandHandler> handlers)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, IWebCommandHandler> _handlers = handlers.ToDictionary(x => x.Command, StringComparer.Ordinal);

    public async Task<WebBridgeResponse> DispatchAsync(string json, CancellationToken cancellationToken = default)
    {
        WebBridgeRequest? request;
        try { request = JsonSerializer.Deserialize<WebBridgeRequest>(json, JsonOptions); }
        catch (JsonException) { return Error(string.Empty, "InvalidJson", "The request is not valid JSON."); }
        if (request is null) return Error(string.Empty, "InvalidRequest", "The request is empty.");
        var requestId = request.RequestId?.Trim() ?? string.Empty;
        if (request.ProtocolVersion != WebBridgeProtocol.Version) return Error(requestId, "ProtocolMismatch", "The protocol version is not supported.");
        if (requestId.Length is 0 or > 64 || !Guid.TryParse(requestId, out _)) return Error(string.Empty, "InvalidRequestId", "A UUID request ID is required.");
        if (request.Command is null || !_handlers.TryGetValue(request.Command, out var handler)) return Error(requestId, "UnknownCommand", "The command is not allowed.");
        try { return new(WebBridgeProtocol.Version, requestId, true, await handler.HandleAsync(request.Payload, cancellationToken), null); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Error(requestId, "Cancelled", "The request was cancelled."); }
        catch { return Error(requestId, "HandlerFailed", "The host could not complete the request."); }
    }

    private static WebBridgeResponse Error(string requestId, string code, string message) =>
        new(WebBridgeProtocol.Version, requestId, false, new { }, new(code, message));
}
