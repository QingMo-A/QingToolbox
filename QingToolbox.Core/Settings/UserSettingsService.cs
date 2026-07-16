using System.Diagnostics;
using System.Text.Json;

namespace QingToolbox.Core.Settings;

public sealed class UserSettingsService : IDisposable
{
    private const int MaximumCorruptBackups = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public UserSettingsService(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        SettingsPath = Path.GetFullPath(settingsPath);
    }

    public string SettingsPath { get; }

    public async Task<UserSettings> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task UpdateAsync(
        Action<UserSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            update(settings);
            settings.Normalize();
            await SaveCoreAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<UserSettings> ReadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(SettingsPath)) return new UserSettings();

        try
        {
            await using var stream = new FileStream(
                SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<UserSettings>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? new UserSettings();
            settings.Normalize();
            return settings;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not read QingToolbox user settings: {exception.GetType().Name}");
            PreserveCorruptSettings();
            return new UserSettings();
        }
    }

    private async Task SaveCoreAsync(UserSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(SettingsPath)
            ?? throw new InvalidOperationException("The settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(SettingsPath)}.tmp.{Guid.NewGuid():N}");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(SettingsPath))
            {
                try { File.Replace(temporaryPath, SettingsPath, null, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException) { File.Move(temporaryPath, SettingsPath, overwrite: true); }
                catch (IOException) { File.Move(temporaryPath, SettingsPath, overwrite: true); }
            }
            else
            {
                File.Move(temporaryPath, SettingsPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void PreserveCorruptSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var directory = Path.GetDirectoryName(SettingsPath)!;
            var backupPath = Path.Combine(
                directory,
                $"settings.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.json");
            File.Move(SettingsPath, backupPath);

            foreach (var stale in Directory.EnumerateFiles(directory, "settings.corrupt-*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(MaximumCorruptBackups))
                File.Delete(stale);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not preserve corrupt QingToolbox settings: {exception.GetType().Name}");
        }
    }

    public void Dispose() => _gate.Dispose();
}
