using Microsoft.Extensions.DependencyInjection;
using QingToolbox.Core.Updates;
using QingToolbox.Shell.Startup;
using QingToolbox.Shell.WebShell;

namespace QingToolbox.Shell.Services;

public sealed record ModuleTransactionRecoveryOutcome(
    int Recovered,
    int CleanupCompleted,
    int RecoveryRequired,
    IReadOnlyList<string> BlockedModuleIds,
    bool HasUnattributedFailure)
{
    public bool IsDegraded => RecoveryRequired > 0 || HasUnattributedFailure;
}

public sealed record DeferredModuleRuntimeRestoreOutcome(
    int Restored,
    int Blocked,
    int Failed);

internal static class ModuleUpdateTransactionHostPolicy
{
    internal static bool SupportsTransactions(ApplicationExecutionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.Kind is ApplicationEnvironmentKind.Production or
            ApplicationEnvironmentKind.Development or ApplicationEnvironmentKind.ModuleTest;
    }

    internal static bool SupportsWebInstall(ApplicationExecutionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.Kind is ApplicationEnvironmentKind.Production or
            ApplicationEnvironmentKind.Development;
    }
}

internal static class ModuleUpdateTransactionHostRegistration
{
    internal static void AddTransactionServices(
        IServiceCollection services,
        ApplicationExecutionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!ModuleUpdateTransactionHostPolicy.SupportsTransactions(environment)) return;

        services.AddSingleton(provider => new ModuleUpdateTransactionService(
            environment.Kind.ToString(),
            provider.GetRequiredService<ApplicationPaths>().UserModulesDirectory,
            provider.GetRequiredService<ApplicationPaths>().ModuleTransactionsDirectory,
            ModuleUpdateIdentity.ModuleApiVersion,
            provider.GetRequiredService<IQmodVerifiedStagingAttestor>(),
            provider.GetRequiredService<IModuleUpdateRuntimeCoordinator>(),
            entry =>
            {
                var message = $"{entry.EventName}; module={entry.ModuleId}; " +
                              $"source={entry.SourceVersion}; target={entry.TargetVersion}; " +
                              $"transaction={entry.TransactionIdPrefix}; state={entry.State}; " +
                              $"failure={entry.FailureCode}.";
                var logger = provider.GetRequiredService<SessionLogService>();
                if (entry.FailureCode == ModuleUpdateTransactionFailureCode.None)
                    logger.Information("ModuleTransaction", message);
                else
                    logger.Warning("ModuleTransaction", message);
            }));
        services.AddSingleton<GatedModuleUpdateTransactionCoordinator>();
    }

    internal static void AddWebInstallServices(
        IServiceCollection services,
        ApplicationExecutionEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!ModuleUpdateTransactionHostPolicy.SupportsWebInstall(environment)) return;

        services.AddSingleton<IWebModuleUpdateInstallOperations, WebModuleUpdateInstallOperations>();
        services.AddSingleton<IWebCommandHandler, WebModuleInstallVerifiedUpdateCommandHandler>();
    }
}

/// <summary>
/// The only host-side entry for verified module-update transaction execution.
/// Its per-module lease closes the VerifyUnloaded-to-rename race against Shell
/// commands in every supported host environment.
/// </summary>
public sealed class GatedModuleUpdateTransactionCoordinator(
    ModuleUpdateTransactionService transactions,
    ModuleTransactionRecoveryGate gate)
{
    public async Task<ModuleUpdateTransactionResult> ExecuteAsync(
        ModuleUpdateTransactionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var moduleId = input.VerifiedStaging.ModuleId;
        await using var lease = await gate.EnterModuleUpdateAsync(moduleId, cancellationToken);
        var result = await transactions.ExecuteAsync(input, cancellationToken).ConfigureAwait(false);
        if (result.State == ModuleUpdateTransactionState.RecoveryRequired ||
            result.FailureCode == ModuleUpdateTransactionFailureCode.RecoveryRequired)
        {
            gate.BlockModule(moduleId, result.FailureCode.ToString());
        }

        return result;
    }
}

/// <summary>
/// Runs after the first Shell presentation and before discovery. All exit paths
/// publish a gate decision so a degraded recovery can never leave module
/// commands waiting forever.
/// </summary>
public sealed class ModuleTransactionRecoveryCoordinator(
    ApplicationExecutionEnvironment environment,
    ModuleTransactionRecoveryGate gate,
    ModuleUpdateRuntimeCoordinator runtimeAdapter,
    IServiceProvider services,
    SessionLogService sessionLog)
{
    public async Task<ModuleTransactionRecoveryOutcome> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        sessionLog.Information("ModuleRecovery",
            $"Module transaction recovery gate started. Environment={environment.Kind}.");

        try
        {
            ModuleUpdateRecoveryResult result;
            using (runtimeAdapter.BeginStartupRecoveryDeferral())
            {
                result = await Task.Run(async () =>
                {
                    var transactionService = services.GetRequiredService<ModuleUpdateTransactionService>();
                    return await transactionService.RecoverAsync(cancellationToken).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }

            var blocked = result.RecoveryRequiredModuleIds.Order(StringComparer.Ordinal).ToArray();
            gate.CompleteRecovery(blocked, result.HasUnattributedRecoveryFailure);
            foreach (var issue in result.Issues)
            {
                sessionLog.Warning("ModuleRecovery",
                    $"Module recovery requires attention; module={issue.ModuleId ?? "unattributed"}; " +
                    $"transaction={ShortId(issue.TransactionId)}; state=RecoveryRequired; " +
                    $"failure={issue.FailureCode}.");
            }

            var outcome = new ModuleTransactionRecoveryOutcome(
                result.Recovered,
                result.CleanupCompleted,
                result.RecoveryRequired,
                blocked,
                result.HasUnattributedRecoveryFailure);
            LogCompletion(outcome);
            return outcome;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            gate.CompleteRecovery([], true);
            throw;
        }
        catch (Exception exception)
        {
            gate.CompleteRecovery([], true);
            var outcome = new ModuleTransactionRecoveryOutcome(0, 0, 1, [], true);
            sessionLog.Warning("ModuleRecovery",
                $"Module transaction recovery completed fail-closed; environment={environment.Kind}; " +
                $"failure={exception.GetType().Name}.");
            return outcome;
        }
    }

    public async Task<DeferredModuleRuntimeRestoreOutcome> RestoreDeferredRuntimeIntentsAsync(
        CancellationToken cancellationToken = default)
    {
        var restored = 0;
        var blocked = 0;
        var failed = 0;
        foreach (var pair in runtimeAdapter.DrainDeferredRuntimeIntents()
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readiness = gate.Consumer.GetReadiness(pair.Key);
            if (!readiness.CanExecute)
            {
                blocked++;
                continue;
            }

            try
            {
                await using var lease = await gate.Consumer.EnterExecutionAsync(
                    pair.Key, cancellationToken).ConfigureAwait(false);
                if (await runtimeAdapter.RestoreRuntimeStateAsync(
                        pair.Value, cancellationToken).ConfigureAwait(false))
                {
                    restored++;
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ModuleExecutionBlockedException)
            {
                blocked++;
                continue;
            }
            catch (Exception exception)
            {
                sessionLog.Warning("ModuleRecovery",
                    $"Deferred runtime intent restore failed; module={pair.Key}; " +
                    $"failure={exception.GetType().Name}.");
            }

            failed++;
            gate.BlockModule(pair.Key, "DeferredRuntimeRestoreFailed");
        }

        sessionLog.Information("ModuleRecovery",
            $"Deferred runtime intent restore completed; restored={restored}; " +
            $"blocked={blocked}; failed={failed}.");
        return new(restored, blocked, failed);
    }

    private void LogCompletion(ModuleTransactionRecoveryOutcome outcome)
    {
        var message = $"Module transaction recovery completed; environment={environment.Kind}; " +
                      $"recovered={outcome.Recovered}; cleanup={outcome.CleanupCompleted}; " +
                      $"required={outcome.RecoveryRequired}; blocked={outcome.BlockedModuleIds.Count}; " +
                      $"unattributed={outcome.HasUnattributedFailure}.";
        if (outcome.IsDegraded)
        {
            sessionLog.Warning("ModuleRecovery", message);
        }
        else
        {
            sessionLog.Information("ModuleRecovery", message);
        }
    }

    private static string ShortId(Guid? transactionId) =>
        transactionId is null ? "unknown" : transactionId.Value.ToString("N")[..12];
}
