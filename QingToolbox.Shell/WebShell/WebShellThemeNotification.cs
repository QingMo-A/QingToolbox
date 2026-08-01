using System.Text.Json;

namespace QingToolbox.Shell.WebShell;

internal enum WebShellThemeMode
{
    System,
    Light,
    Dark
}

internal static class WebShellThemeNotification
{
    private const string Kind = "qing.ui.theme";

    public static bool TryParse(string json, out WebShellThemeMode mode)
    {
        mode = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.GetRawText().Length > 128)
                return false;

            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 2 ||
                !root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() != Kind ||
                !root.TryGetProperty("mode", out var value) || value.ValueKind != JsonValueKind.String)
                return false;

            return value.GetString() switch
            {
                "system" => Set(WebShellThemeMode.System, out mode),
                "light" => Set(WebShellThemeMode.Light, out mode),
                "dark" => Set(WebShellThemeMode.Dark, out mode),
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Set(WebShellThemeMode value, out WebShellThemeMode mode)
    {
        mode = value;
        return true;
    }
}
