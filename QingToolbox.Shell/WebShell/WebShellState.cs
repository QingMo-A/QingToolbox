using QingToolbox.Shell.Startup;

namespace QingToolbox.Shell.WebShell;

public enum WebShellAvailability { Disabled, Initializing, Ready, NativeFallback }

public sealed class WebShellState(ApplicationExecutionEnvironment environment)
{
    public bool IsEnvironmentAllowed => environment.IsDevelopment;
    public WebShellAvailability Availability { get; private set; } = environment.IsDevelopment
        ? WebShellAvailability.Initializing : WebShellAvailability.Disabled;
    public string? FailureCode { get; private set; }
    public int ProcessRecoveryAttempts { get; private set; }
    public void MarkReady() { Availability = WebShellAvailability.Ready; FailureCode = null; }
    public void FallBack(string failureCode) { Availability = WebShellAvailability.NativeFallback; FailureCode = failureCode; }
    public bool TryBeginProcessRecovery() => ProcessRecoveryAttempts++ == 0;
}
