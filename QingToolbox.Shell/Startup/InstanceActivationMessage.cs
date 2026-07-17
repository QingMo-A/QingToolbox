namespace QingToolbox.Shell.Startup;

public enum InstanceActivationMessage { Activate, StartupProbe }

public sealed record InstanceActivationRequest(InstanceActivationMessage Message, Guid? StartupTestId = null);

public static class InstanceActivationProtocol
{
    public const int MaximumMessageLength = 96;

    public static string Serialize(InstanceActivationRequest request) => request.StartupTestId is { } id
        ? $"{request.Message}:{id:D}"
        : request.Message.ToString();

    public static bool TryParseRequest(string? value, out InstanceActivationRequest request)
    {
        request = new(default);
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumMessageLength ||
            value.Any(character => character is '\r' or '\n')) return false;
        var separator = value.IndexOf(':');
        var kindText = separator < 0 ? value : value[..separator];
        if (!Enum.TryParse<InstanceActivationMessage>(kindText, false, out var kind) || kindText != kind.ToString()) return false;
        Guid? testId = null;
        if (separator >= 0)
        {
            if (kind != InstanceActivationMessage.StartupProbe ||
                !Guid.TryParseExact(value[(separator + 1)..], "D", out var parsed)) return false;
            testId = parsed;
        }
        request = new(kind, testId);
        return true;
    }

    public static bool TryParse(string? value, out InstanceActivationMessage message)
    {
        message = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumMessageLength ||
            value.Any(character => character is '\r' or '\n')) return false;
        if (!TryParseRequest(value, out var request)) return false;
        message = request.Message;
        return true;
    }
}
