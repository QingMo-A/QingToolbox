namespace QingToolbox.Shell.Services;

public readonly record struct ExitCleanupStage(string Name, Func<Task> Execute);

public static class ExitCleanupPipeline
{
    public static async Task RunAsync(
        IEnumerable<ExitCleanupStage> stages,
        Action shutdown,
        Action<string, Exception>? failureObserver = null)
    {
        try
        {
            foreach (var stage in stages)
            {
                try { await stage.Execute(); }
                catch (Exception exception) { failureObserver?.Invoke(stage.Name, exception); }
            }
        }
        finally
        {
            try { shutdown(); }
            catch (Exception exception) { failureObserver?.Invoke("application shutdown", exception); }
        }
    }
}
