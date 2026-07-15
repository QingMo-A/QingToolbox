namespace QingToolbox.Shell.Startup;

public enum InstanceActivationMessage { Activate, StartupProbe }

public static class InstanceActivationProtocol
{
    public const int MaximumMessageLength = 32;

    public static bool TryParse(string? value, out InstanceActivationMessage message)
    {
        message = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumMessageLength ||
            value.Any(character => character is '\r' or '\n')) return false;
        return Enum.TryParse(value, ignoreCase: false, out message) &&
               value == message.ToString();
    }
}
