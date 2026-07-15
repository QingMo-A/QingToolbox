using System.IO;
using System.Text.Json;
using QingToolbox.Modules.PowerGuard.Models;

namespace QingToolbox.Modules.PowerGuard.Services;

public sealed class PowerGuardSettingsStore(string dataDirectory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(dataDirectory, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<PowerGuardSettings> ReadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!File.Exists(_path)) return new PowerGuardSettings();
            try
            {
                await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                var value = await JsonSerializer.DeserializeAsync<PowerGuardSettings>(stream, JsonOptions, token) ?? new();
                value.Normalize();
                return value;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
            {
                BackupCorruptFile();
                return new PowerGuardSettings();
            }
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(PowerGuardSettings settings, CancellationToken token = default)
    {
        settings.Normalize();
        await _gate.WaitAsync(token);
        var temporary = $"{_path}.tmp.{Guid.NewGuid():N}";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, token);
                await stream.FlushAsync(token);
            }
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            _gate.Release();
        }
    }

    private void BackupCorruptFile()
    {
        if (!File.Exists(_path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var backup = Path.Combine(Path.GetDirectoryName(_path)!, $"settings.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.json");
        File.Move(_path, backup, true);
        foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, "settings.corrupt-*.json")
                     .OrderByDescending(File.GetCreationTimeUtc).Skip(3)) File.Delete(old);
    }

    public void Dispose() => _gate.Dispose();
}
