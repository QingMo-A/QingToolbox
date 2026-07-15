using System.IO;
using System.Text.Json;
using QingToolbox.Modules.PowerGuard.Models;

namespace QingToolbox.Modules.PowerGuard.Services;

public sealed class PowerGuardEventStore(string dataDirectory) : IDisposable
{
    public event EventHandler? EventAppended;
    private const long MaximumBytes = 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(dataDirectory, "events.jsonl");
    private readonly string _previous = Path.Combine(dataDirectory, "events.previous.jsonl");

    public async Task AppendAsync(string type, string? detail = null, CancellationToken token = default)
    {
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length >= MaximumBytes)
                {
                    File.Move(_path, _previous, true);
                }
                var safeDetail = string.IsNullOrWhiteSpace(detail) ? null : detail.Length > 160 ? detail[..160] : detail;
                await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(new GuardEvent(DateTimeOffset.UtcNow, type, safeDetail)) + Environment.NewLine, token);
                EventAppended?.Invoke(this, EventArgs.Empty);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* Event diagnostics must never stop the guard. */ }
    }

    public async Task<IReadOnlyList<GuardEvent>> ReadRecentAsync(int count = 20, CancellationToken token = default)
    {
        if (!File.Exists(_path)) return [];
        var lines = await File.ReadAllLinesAsync(_path, token);
        return lines.TakeLast(count).Select(line => { try { return JsonSerializer.Deserialize<GuardEvent>(line); } catch { return null; } }).Where(x => x is not null).Cast<GuardEvent>().ToArray();
    }
    public void Dispose() => _gate.Dispose();
}
