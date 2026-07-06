using System.Text.Json;

namespace QingToolbox.Core.Localization;

public sealed class UserSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "QingToolbox",
        "settings.json");

    public async Task<LanguageSettings> LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new LanguageSettings();
            }

            await using var stream = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<LanguageSettings>(
                       stream,
                       JsonOptions) ??
                   new LanguageSettings();
        }
        catch
        {
            return new LanguageSettings();
        }
    }

    public async Task SaveAsync(LanguageSettings settings)
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions);
    }
}
