using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.WebShell;

public enum WebShellAvailability
{
    Disabled, Native, Initializing, Navigating, AwaitingReady, Ready,
    Recovering, NativeFallback, Disposing, Disposed
}

public sealed class WebShellState(ApplicationExecutionEnvironment environment)
{
    public bool IsEnvironmentAllowed => environment.IsDevelopment;
    public WebShellAvailability Availability { get; private set; } = environment.IsDevelopment
        ? WebShellAvailability.Native : WebShellAvailability.Disabled;
    public string? FailureCode { get; private set; }
    public int ProcessRecoveryAttempts { get; private set; }
    public bool IsReady => Availability == WebShellAvailability.Ready;

    public void BeginInitialization() => Transition(WebShellAvailability.Initializing);
    public void BeginNavigation() => Transition(WebShellAvailability.Navigating);
    public void AwaitReady() => Transition(WebShellAvailability.AwaitingReady);
    public void MarkReady() { Transition(WebShellAvailability.Ready); FailureCode = null; }
    public void BeginRecovery() => Transition(WebShellAvailability.Recovering);
    public void FallBack(string failureCode) { FailureCode = failureCode; Transition(WebShellAvailability.NativeFallback); }
    public bool TryBeginProcessRecovery() { if (ProcessRecoveryAttempts != 0) return false; ProcessRecoveryAttempts++; BeginRecovery(); return true; }
    public void BeginDisposal() => Transition(WebShellAvailability.Disposing);
    public void MarkDisposed() => Transition(WebShellAvailability.Disposed);

    private void Transition(WebShellAvailability next) => Availability = next;
}
