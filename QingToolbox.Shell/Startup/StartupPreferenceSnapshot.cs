using System.Text.Json;
using System.IO;
using QingToolbox.Core.Settings;

namespace QingToolbox.Shell.Startup;

public sealed record StartupPreferenceSnapshot(
    bool LaunchAtLogin,
    StartupPresentationMode PresentationMode,
    MainWindowCloseBehavior CloseBehavior,
    string Language)
{
    public static StartupPreferenceSnapshot SafeDefault(bool startup) => new(
        false,
        startup ? StartupPresentationMode.FloatingBadge : StartupPresentationMode.MainWindow,
        MainWindowCloseBehavior.Ask,
        "system");
}

public sealed class StartupPreferenceReader
{
    public const int MaximumSettingsBytes = 1024 * 1024;
    public async Task<StartupPreferenceSnapshot> ReadAsync(string path, bool startup, CancellationToken token = default)
    {
        var fallback = StartupPreferenceSnapshot.SafeDefault(startup);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumSettingsBytes) return fallback;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = document.RootElement;
            var launch = root.TryGetProperty(nameof(UserSettings.LaunchAtLogin), out var launchElement) && launchElement.ValueKind is JsonValueKind.True;
            var language = root.TryGetProperty(nameof(UserSettings.Language), out var languageElement) && languageElement.ValueKind == JsonValueKind.String
                ? languageElement.GetString() ?? "system" : "system";
            var mode = ReadEnum(root, nameof(UserSettings.StartupPresentationMode), fallback.PresentationMode);
            var close = ReadEnum(root, nameof(UserSettings.MainWindowCloseBehavior), fallback.CloseBehavior);
            return new(launch, mode, close, string.IsNullOrWhiteSpace(language) ? "system" : language);
        }
        catch (OperationCanceledException) { throw; }
        catch { return fallback; }
    }

    private static T ReadEnum<T>(JsonElement root, string name, T fallback) where T : struct, Enum
    {
        if (!root.TryGetProperty(name, out var element)) return fallback;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number) && Enum.IsDefined(typeof(T), number))
            return (T)Enum.ToObject(typeof(T), number);
        if (element.ValueKind == JsonValueKind.String && Enum.TryParse<T>(element.GetString(), true, out var parsed)) return parsed;
        return fallback;
    }
}
