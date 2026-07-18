using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace QingToolbox.Shell.Services;

public enum SessionLogLevel { Information, Warning, Error }

public sealed record SessionLogEntry(DateTimeOffset Timestamp, SessionLogLevel Level, string Category, string Message)
{
    public string TimeDisplay => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    public string LevelDisplay => Level.ToString();
}

public sealed class SessionLogService : IDisposable
{
    private const int MaximumVisibleEntries = 2000;
    private const int MaximumRetainedFiles = 30;
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public SessionLogService(ApplicationPaths paths, TimeProvider timeProvider)
    {
        LogsDirectory = Path.GetFullPath(paths.LogsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        RemoveExpiredLogs();
        var started = timeProvider.GetLocalNow();
        CurrentLogPath = Path.Combine(LogsDirectory, $"qingtoolbox-{started:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        _writer = new StreamWriter(new FileStream(CurrentLogPath, FileMode.CreateNew, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string LogsDirectory { get; }
    public string CurrentLogPath { get; }
    public ObservableCollection<SessionLogEntry> Entries { get; } = [];

    public void Information(string category, string message) => Write(SessionLogLevel.Information, category, message);
    public void Warning(string category, string message) => Write(SessionLogLevel.Warning, category, message);
    public void Error(string category, string message, Exception? exception = null) =>
        Write(SessionLogLevel.Error, category, exception is null ? message : $"{message} ({exception.GetType().Name}: {exception.Message})");

    public void Write(SessionLogLevel level, string category, string message)
    {
        if (_disposed) return;
        var entry = new SessionLogEntry(DateTimeOffset.Now, level, Sanitize(category), Sanitize(message));
        lock (_sync)
        {
            if (_disposed) return;
            _writer.WriteLine($"{entry.Timestamp:O}\t{entry.Level}\t{entry.Category}\t{entry.Message}");
        }
        Debug.WriteLine($"[{entry.Level}] {entry.Category}: {entry.Message}");
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) AddVisible(entry);
        else _ = dispatcher.BeginInvoke(() => AddVisible(entry));
    }

    public void ClearVisible() => Entries.Clear();

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _writer.WriteLine($"{DateTimeOffset.Now:O}\tInformation\tApplication\tSession log closed.");
            _disposed = true;
            _writer.Dispose();
        }
    }

    private void AddVisible(SessionLogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaximumVisibleEntries) Entries.RemoveAt(0);
    }

    private void RemoveExpiredLogs()
    {
        try
        {
            foreach (var file in new DirectoryInfo(LogsDirectory).EnumerateFiles("qingtoolbox-*.log")
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaximumRetainedFiles - 1))
                file.Delete();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Log retention cleanup degraded: {exception.GetType().Name}");
        }
    }

    private static string Sanitize(string value)
    {
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? sanitized
            : sanitized.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }
}
