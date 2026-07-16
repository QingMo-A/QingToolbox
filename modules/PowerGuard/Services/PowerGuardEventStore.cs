using System.IO;
using System.Text.Json;
using QingToolbox.Modules.PowerGuard.Models;

namespace QingToolbox.Modules.PowerGuard.Services;

public sealed class PowerGuardEventStore : IDisposable
{
    private readonly TimeProvider _timeProvider;
    public event EventHandler? EventAppended;
    internal void ClearSubscribers()=>EventAppended=null;
    private const long MaximumBytes = 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private readonly string _previous;

    public PowerGuardEventStore(string dataDirectory, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _path = Path.Combine(dataDirectory, "events.jsonl");
        _previous = Path.Combine(dataDirectory, "events.previous.jsonl");
    }

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
                await File.AppendAllTextAsync(_path, JsonSerializer.Serialize(new GuardEvent(_timeProvider.GetUtcNow(), type, safeDetail)) + Environment.NewLine, token);
                EventAppended?.Invoke(this, EventArgs.Empty);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* Event diagnostics must never stop the guard. */ }
    }

    public async Task<IReadOnlyList<GuardEvent>> ReadRecentAsync(int count = 20, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (!File.Exists(_path)) return [];
            await using var stream=new FileStream(_path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,true);
            var take=(int)Math.Min(stream.Length,64*1024);stream.Seek(-take,SeekOrigin.End);var buffer=new byte[take];await stream.ReadExactlyAsync(buffer,token);
            var text=System.Text.Encoding.UTF8.GetString(buffer);var lines=text.Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries);if(stream.Length>take&&lines.Length>0)lines=lines[1..];
            return lines.TakeLast(Math.Clamp(count,1,20)).Select(line=>{try{return JsonSerializer.Deserialize<GuardEvent>(line);}catch{return null;}}).Where(x=>x is not null).Cast<GuardEvent>().ToArray();
        }
        catch(OperationCanceledException){throw;}catch(Exception e){System.Diagnostics.Debug.WriteLine(e.GetType().Name);return [];}
        finally{_gate.Release();}
    }
    public void Dispose() => _gate.Dispose();
}
