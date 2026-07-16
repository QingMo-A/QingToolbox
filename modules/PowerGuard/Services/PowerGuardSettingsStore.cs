using System.Diagnostics;
using System.IO;
using System.Text.Json;
using QingToolbox.Modules.PowerGuard.Models;

namespace QingToolbox.Modules.PowerGuard.Services;
public interface IAtomicFileReplacer{void Replace(string temporaryPath,string destinationPath);}
public sealed class AtomicFileReplacer:IAtomicFileReplacer
{
    public void Replace(string temporaryPath,string destinationPath)
    {
        if(File.Exists(destinationPath))File.Replace(temporaryPath,destinationPath,null,true);
        else File.Move(temporaryPath,destinationPath);
    }
}
public sealed class PowerGuardSettingsStore(string dataDirectory,IAtomicFileReplacer? replacer=null,TimeProvider? timeProvider=null):IDisposable
{
    private readonly SemaphoreSlim _gate=new(1,1);private readonly string _path=Path.Combine(dataDirectory,"settings.json");private readonly IAtomicFileReplacer _replacer=replacer??new AtomicFileReplacer();private readonly TimeProvider _timeProvider=timeProvider??TimeProvider.System;private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    public async Task<PowerGuardSettings> ReadAsync(CancellationToken token=default){await _gate.WaitAsync(token);try{return await ReadCoreAsync(token);}finally{_gate.Release();}}
    public async Task<PowerGuardSettings> UpdateAsync(Action<PowerGuardSettings> update,CancellationToken token=default){await _gate.WaitAsync(token);try{var value=await ReadCoreAsync(token);update(value);value.Normalize();await WriteCoreAsync(value,token);return Clone(value);}finally{_gate.Release();}}
    public async Task WriteSnapshotAsync(PowerGuardSettings settings,CancellationToken token=default){await _gate.WaitAsync(token);try{var value=Clone(settings);value.Normalize();await WriteCoreAsync(value,token);}finally{_gate.Release();}}
    public Task WriteAsync(PowerGuardSettings settings,CancellationToken token=default)=>WriteSnapshotAsync(settings,token);
    private async Task<PowerGuardSettings> ReadCoreAsync(CancellationToken token)
    {
        if(!File.Exists(_path))return new();try{await using var stream=new FileStream(_path,FileMode.Open,FileAccess.Read,FileShare.Read,4096,true);var value=await JsonSerializer.DeserializeAsync<PowerGuardSettings>(stream,JsonOptions,token)??new();value.Normalize();return value;}
        catch(OperationCanceledException){throw;}catch(Exception e)when(e is JsonException or IOException or UnauthorizedAccessException){Debug.WriteLine(e.GetType().Name);TryBackupCorrupt();return new();}
    }
    private async Task WriteCoreAsync(PowerGuardSettings value,CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);var temporary=$"{_path}.tmp.{Guid.NewGuid():N}";try{await using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,true)){await JsonSerializer.SerializeAsync(stream,value,JsonOptions,token);await stream.FlushAsync(token);stream.Flush(true);}token.ThrowIfCancellationRequested();_replacer.Replace(temporary,_path);}finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(Exception e){Debug.WriteLine(e.GetType().Name);}}
    }
    private void TryBackupCorrupt(){try{if(!File.Exists(_path))return;var dir=Path.GetDirectoryName(_path)!;var backup=Path.Combine(dir,$"settings.corrupt-{_timeProvider.GetUtcNow():yyyyMMddHHmmssfff}.json");File.Copy(_path,backup,false);foreach(var old in Directory.EnumerateFiles(dir,"settings.corrupt-*.json").OrderByDescending(File.GetCreationTimeUtc).Skip(3))try{File.Delete(old);}catch(Exception e){Debug.WriteLine(e.GetType().Name);}}catch(Exception e){Debug.WriteLine(e.GetType().Name);}}
    public static PowerGuardSettings Clone(PowerGuardSettings s)=>new(){GuardEnabled=s.GuardEnabled,StartupGraceSeconds=s.StartupGraceSeconds,OfflineConfirmationSeconds=s.OfflineConfirmationSeconds,ShutdownCountdownSeconds=s.ShutdownCountdownSeconds,RecoveryConfirmationSeconds=s.RecoveryConfirmationSeconds,PowerAction=s.PowerAction,SuppressedUntilConnectivityRestored=s.SuppressedUntilConnectivityRestored,ShowRecoveryNotification=s.ShowRecoveryNotification};public void Dispose()=>_gate.Dispose();
}
