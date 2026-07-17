using System.Text.Json;
using System.IO;

namespace QingToolbox.Shell.Startup;

public enum StartupPhase { ProcessEntry, InstanceReady, MinimalServicesReady, NotificationAreaReady, PresentationReady, RegistrationHealthReady, ModuleDiscoveryComplete, AuthorizedModulesRestored, Ready, Failed, Exiting }
public enum StartupExitCode { Success=0, InvalidArguments=10, RegistrationFailure=11, FatalInitializationFailure=12, SingleInstanceDeliveryFailure=13 }

public sealed record StartupHealthRecord
{
    public int SchemaVersion { get; init; } = 1;
    public Guid AttemptId { get; init; } = Guid.NewGuid();
    public StartupLaunchSource Source { get; set; }
    public DateTimeOffset ProcessStartedAt { get; init; }
    public DateTimeOffset? InstanceReadyAt { get; set; }
    public DateTimeOffset? NotificationAreaReadyAt { get; set; }
    public DateTimeOffset? PresentationReadyAt { get; set; }
    public DateTimeOffset? ModuleDiscoveryCompletedAt { get; set; }
    public DateTimeOffset? AuthorizedModulesRestoredAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public StartupPhase? FailurePhase { get; set; }
    public string? FailureCode { get; set; }
    public int? ProcessExitCode { get; set; }
    public bool WasSecondaryInstance { get; set; }
    public Dictionary<string,long> ElapsedMilliseconds { get; init; } = [];
}

public sealed class StartupHealthJournal(string path, TimeProvider timeProvider)
{
    public const int MaximumRecords=10, MaximumFileBytes=256*1024;
    private readonly SemaphoreSlim _gate=new(1,1);
    private readonly long _origin=System.Diagnostics.Stopwatch.GetTimestamp();
    private readonly StartupHealthRecord _current=new(){ProcessStartedAt=timeProvider.GetUtcNow()};
    public StartupHealthRecord Current => _current;
    public void SetSource(StartupLaunchSource source) => _current.Source=source;
    public void Mark(StartupPhase phase, string? failureCode=null, int? exitCode=null)
    {
        var now=timeProvider.GetUtcNow(); var elapsed=System.Diagnostics.Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;
        _current.ElapsedMilliseconds[phase.ToString()]=(long)elapsed;
        switch(phase){case StartupPhase.InstanceReady:_current.InstanceReadyAt=now;break;case StartupPhase.NotificationAreaReady:_current.NotificationAreaReadyAt=now;break;case StartupPhase.PresentationReady:_current.PresentationReadyAt=now;break;case StartupPhase.ModuleDiscoveryComplete:_current.ModuleDiscoveryCompletedAt=now;break;case StartupPhase.AuthorizedModulesRestored:_current.AuthorizedModulesRestoredAt=now;break;case StartupPhase.Ready:_current.ReadyAt=now;break;case StartupPhase.Failed:_current.FailedAt=now;_current.FailurePhase=phase;_current.FailureCode=failureCode;_current.ProcessExitCode=exitCode;break;}
        _=PersistSafelyAsync();
    }
    public void Fail(StartupPhase failurePhase,string failureCode,int exitCode)
    {
        _current.FailedAt=timeProvider.GetUtcNow();
        _current.FailurePhase=failurePhase;
        _current.FailureCode=failureCode;
        _current.ProcessExitCode=exitCode;
        _current.ElapsedMilliseconds[StartupPhase.Failed.ToString()]=(long)System.Diagnostics.Stopwatch.GetElapsedTime(_origin).TotalMilliseconds;
        _=PersistSafelyAsync();
    }
    public async Task<IReadOnlyList<StartupHealthRecord>> ReadAsync(CancellationToken token=default)
    {
        try{var info=new FileInfo(path);if(!info.Exists||info.Length>MaximumFileBytes)return[];await using var stream=File.OpenRead(path);return await JsonSerializer.DeserializeAsync<List<StartupHealthRecord>>(stream,cancellationToken:token)??[];}catch{return[];}
    }
    private async Task PersistSafelyAsync(){try{await _gate.WaitAsync();try{var records=(await ReadAsync()).Where(x=>x.AttemptId!=_current.AttemptId).Append(_current).TakeLast(MaximumRecords).ToArray();var directory=Path.GetDirectoryName(path)!;Directory.CreateDirectory(directory);var temp=path+".tmp."+Guid.NewGuid().ToString("N");await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(records,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,path,true);}finally{_gate.Release();}}catch{}}
}
