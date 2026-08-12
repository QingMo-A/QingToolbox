using System.Text;
using System.Text.Json;
using QingToolbox.Abstractions.Modules;

namespace QingToolbox.ModuleHost;

internal sealed class WebModuleBridgeDispatcher(IWebToolModule module)
{
    private const int MaximumMessageBytes = 64 * 1024;

    public async Task<string?> DispatchAsync(string message, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(message) > MaximumMessageBytes) return null;
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !string.Equals(type.GetString(), "invoke", StringComparison.Ordinal) ||
                !root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                return null;
            id = idElement.GetString();
            var method = methodElement.GetString();
            if (!IsValidToken(id) || !IsValidToken(method)) return null;
            JsonElement? payload = root.TryGetProperty("payload", out var payloadElement)
                ? payloadElement.Clone()
                : null;
            var result = await module.HandleWebRequestAsync(method!, payload, cancellationToken);
            return JsonSerializer.Serialize(new { type = "result", id, ok = true, payload = result });
        }
        catch
        {
            return id is null ? null : JsonSerializer.Serialize(new
            {
                type = "result", id, ok = false, error = "Module request failed."
            });
        }
    }

    public string? SerializeEvent(ModuleWebEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(eventArgs.Name) || eventArgs.Name.Length > 128 || eventArgs.Name.Any(char.IsControl)) return null;
        return JsonSerializer.Serialize(new { type = "event", name = eventArgs.Name, payload = eventArgs.Payload });
    }

    private static bool IsValidToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && !value.Any(char.IsControl);
}
