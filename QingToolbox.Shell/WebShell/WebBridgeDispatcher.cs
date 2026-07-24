using System.Text;
using System.Text.Json;

namespace QingToolbox.Shell.WebShell;

public sealed class WebBridgeDispatcher(IEnumerable<IWebCommandHandler> handlers)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyDictionary<string, IWebCommandHandler> _handlers = handlers.ToDictionary(x => x.Command, StringComparer.Ordinal);

    public async Task<WebBridgeDispatchResult> DispatchAsync(string json, WebBridgeRequestContext context,
        CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(json) > WebBridgeProtocol.MaximumRequestBytes)
            return Invalid("", "RequestTooLarge", "The request exceeds the size limit.");
        WebBridgeRequest? request;
        try { request = JsonSerializer.Deserialize<WebBridgeRequest>(json, JsonOptions); }
        catch (JsonException) { return Invalid("", "InvalidJson", "The request is not valid JSON."); }
        if (request is null) return Invalid("", "InvalidRequest", "The request is empty.");
        var requestId = request.RequestId?.Trim() ?? string.Empty;
        if (request.ProtocolVersion != WebBridgeProtocol.Version) return Invalid(requestId, "ProtocolMismatch", "The protocol version is not supported.");
        if (requestId.Length is 0 or > 64 || !Guid.TryParse(requestId, out _)) return Invalid("", "InvalidRequestId", "A UUID request ID is required.");
        if (request.Command is null || request.Command.Length is 0 or > 64 || !_handlers.TryGetValue(request.Command, out var handler))
            return Invalid(requestId, "UnknownCommand", "The command is not allowed.");
        if (request.Payload.ValueKind != JsonValueKind.Object || request.Payload.EnumerateObject().Any(p => !handler.AllowedPayloadProperties.Contains(p.Name)))
            return Invalid(requestId, "InvalidPayload", "The command payload is invalid.");
        try
        {
            context.SessionCancellation.ThrowIfCancellationRequested();
            var payload = await handler.HandleAsync(request.Payload, context, cancellationToken);
            var response = new WebBridgeResponse(WebBridgeProtocol.Version, requestId, true, payload, null);
            return new(request.Command, requestId, request.ProtocolVersion, request.Payload, response);
        }
        catch (WebBridgeValidationException exception) { return Invalid(requestId, exception.Code, exception.Message); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || context.SessionCancellation.IsCancellationRequested) { return Invalid(requestId, "Cancelled", "The request was cancelled."); }
        catch { return Invalid(requestId, "HandlerFailed", "The host could not complete the request."); }
    }

    private static WebBridgeDispatchResult Invalid(string requestId, string code, string message) =>
        new(null, requestId, WebBridgeProtocol.Version, null,
            new(WebBridgeProtocol.Version, requestId, false, new { }, new(code, message)));
}
