using System.Text.Json;
using System.IO;

namespace QingToolbox.Modules.QingTransfer;

public sealed record QingTransferReceiveSettings(
    string? DefaultDirectory = null,
    bool UseDefaultDirectory = false,
    bool AutoAccept = false);

/// <summary>Small, module-local settings file. A bad or unavailable file safely means defaults.</summary>
public sealed class QingTransferReceiveSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;

    public QingTransferReceiveSettingsStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "receive-settings.json");
    }

    public QingTransferReceiveSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new();
                var value = JsonSerializer.Deserialize<QingTransferReceiveSettings>(File.ReadAllText(_path), JsonOptions);
                return value is null ? new() : Normalize(value);
            }
            catch (JsonException) { return new(); }
            catch (IOException) { return new(); }
            catch (UnauthorizedAccessException) { return new(); }
        }
    }

    public void Save(QingTransferReceiveSettings value)
    {
        lock (_gate)
        {
            var temporary = $"{_path}.tmp.{Guid.NewGuid():N}";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(value), JsonOptions));
                if (File.Exists(_path)) File.Replace(temporary, _path, null, true);
                else File.Move(temporary, _path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }

    private static QingTransferReceiveSettings Normalize(QingTransferReceiveSettings value)
    {
        string? directory;
        try { directory = string.IsNullOrWhiteSpace(value.DefaultDirectory) ? null : Path.GetFullPath(value.DefaultDirectory); }
        catch (ArgumentException) { directory = null; }
        catch (NotSupportedException) { directory = null; }
        return value with { DefaultDirectory = directory };
    }
}

internal static class QingTransferReceivePolicy
{
    public static bool TryGetAutomaticDestination(QingTransferReceiveSettings settings, string fileName, out string? destination)
    {
        destination = null;
        if (!settings.AutoAccept || !settings.UseDefaultDirectory || string.IsNullOrWhiteSpace(settings.DefaultDirectory)) return false;
        var directory = settings.DefaultDirectory!;
        if (!Directory.Exists(directory) || !CanWrite(directory) || string.IsNullOrWhiteSpace(Path.GetFileName(fileName))) return false;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 0; index < 1000; index++)
        {
            var suffix = index == 0 ? string.Empty : $" ({index})";
            var candidate = Path.Combine(directory, baseName + suffix + extension);
            if (!File.Exists(candidate)) { destination = candidate; return true; }
        }
        return false;
    }

    private static bool CanWrite(string directory)
    {
        var probe = Path.Combine(directory, $".qingtransfer-write-test-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
            return false;
        }
    }
}
