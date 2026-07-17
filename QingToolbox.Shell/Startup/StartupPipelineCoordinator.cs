using System.Runtime.InteropServices;
using System.IO;
using System.Net.Http;

namespace QingToolbox.Shell.Startup;

public sealed class ModuleDiscoveryException(string message, Exception? innerException = null)
    : IOException(message, innerException);

public sealed record StartupPipelineStage(string Name, Func<CancellationToken, Task> Execute);
public sealed record StartupPipelineStageResult(string Name, StartupPhaseOutcome Outcome, Exception? Error = null);

/// <summary>Executes independent post-presentation stages without allowing an auxiliary failure to suppress Ready.</summary>
public sealed class StartupPipelineCoordinator
{
    public async Task<IReadOnlyList<StartupPipelineStageResult>> RunAsync(
        IEnumerable<StartupPipelineStage> stages,
        CancellationToken cancellationToken = default)
    {
        var results = new List<StartupPipelineStageResult>();
        foreach (var stage in stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await stage.Execute(cancellationToken);
                results.Add(new(stage.Name, StartupPhaseOutcome.Succeeded));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (IsAllowedDegradation(exception))
            {
                results.Add(new(stage.Name, StartupPhaseOutcome.Degraded, exception));
            }
        }
        return results;
    }

    private static bool IsAllowedDegradation(Exception exception) => exception is
        IOException or UnauthorizedAccessException or COMException or HttpRequestException;
}
