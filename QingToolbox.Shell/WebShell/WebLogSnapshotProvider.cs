using QingToolbox.Shell.Services;

namespace QingToolbox.Shell.WebShell;

public interface IWebLogSnapshotSource { IReadOnlyList<WebLogSnapshotEntry> ReadEntries(); }

public sealed class WebLogSnapshotSource(SessionLogService logs) : IWebLogSnapshotSource
{
    public IReadOnlyList<WebLogSnapshotEntry> ReadEntries() => logs.Entries
        .Select(entry => new WebLogSnapshotEntry(entry.Timestamp, entry.Level.ToString(), entry.Category, entry.Message)).ToArray();
}

public sealed class WebLogSnapshotProvider(IWebLogSnapshotSource source, TimeProvider timeProvider)
{
    private const int MaximumEntries = 500;
    public WebLogSnapshot Create()
    {
        var entries = source.ReadEntries();
        return new(timeProvider.GetUtcNow(), entries.Skip(Math.Max(0, entries.Count - MaximumEntries)).ToArray());
    }
}
