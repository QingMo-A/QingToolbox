using System.Text.Json;
using QingToolbox.Shell.Startup;
using QingToolbox.Shell.WebShell;

Console.WriteLine("Verifying Web Shell environment boundary...");
Require(!new WebShellState(ApplicationExecutionEnvironment.Production()).IsEnvironmentAllowed, "Production must disable Web Shell.");
var repositoryRoot = Directory.GetCurrentDirectory();
Require(new WebShellState(ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.Development, "WebShellSmoke", repositoryRoot)).IsEnvironmentAllowed, "Development must allow Web Shell.");
Require(!new WebShellState(ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.ModuleTest, "WebShellSmoke", repositoryRoot)).IsEnvironmentAllowed, "ModuleTest must disable Web Shell.");
var devTools = ApplicationLaunchOptions.Parse(["--environment", "Development", "--profile", "WebShellTools", "--repo-root", repositoryRoot, "--web-devtools"]);
Require(devTools.EnableWebDevTools, "Development must accept explicit Web DevTools configuration.");
try { ApplicationLaunchOptions.Parse(["--environment", "Production", "--web-devtools"]); throw new InvalidOperationException("Production accepted Web DevTools."); }
catch (ArgumentException) { }

Console.WriteLine("Verifying navigation and browser capability denial...");
var navigation = new WebNavigationPolicy();
Require(navigation.IsAllowed(WebNavigationPolicy.StartUri), "Local virtual host must be allowed.");
Require(!navigation.IsAllowed(new Uri("https://example.com/")) && !navigation.IsAllowed(new Uri("http://app.qingtoolbox.local/")), "External and insecure navigation must be denied.");
Require(!navigation.AllowNewWindow(null) && !navigation.AllowDownload(null) && !navigation.AllowPermission(null), "New windows, downloads, and permissions must be denied.");

Console.WriteLine("Verifying Bridge protocol validation and allowlist...");
var dispatcher = new WebBridgeDispatcher([new EchoHandler()]);
var id = Guid.NewGuid().ToString();
var success = await dispatcher.DispatchAsync(JsonSerializer.Serialize(new { protocolVersion = 1, requestId = id, command = "app.ping", payload = new { } }));
Require(success.Success && success.RequestId == id, "Response must match request ID.");
Require(!(await dispatcher.DispatchAsync(JsonSerializer.Serialize(new { protocolVersion = 2, requestId = id, command = "app.ping", payload = new { } }))).Success, "Invalid protocol must be rejected.");
Require(!(await dispatcher.DispatchAsync(JsonSerializer.Serialize(new { protocolVersion = 1, requestId = "", command = "app.ping", payload = new { } }))).Success, "Empty request ID must be rejected.");
var unknown = await dispatcher.DispatchAsync(JsonSerializer.Serialize(new { protocolVersion = 1, requestId = id, command = "module.load", payload = new { path = "C:\\private" } }));
Require(!unknown.Success && unknown.Error?.Code == "UnknownCommand" && !unknown.Error.Message.Contains("C:\\", StringComparison.Ordinal), "Unknown side-effect commands must be rejected without path disclosure.");

Console.WriteLine("Verifying fallback and bounded process recovery...");
var state = new WebShellState(ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.Development, "WebShellFallback", repositoryRoot));
state.FallBack("WebShell.RuntimeMissing");
Require(state.Availability == WebShellAvailability.NativeFallback, "Runtime failure must select native fallback.");
var processState = new WebShellState(ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.Development, "WebShellProcess", repositoryRoot));
Require(processState.TryBeginProcessRecovery() && !processState.TryBeginProcessRecovery(), "Process recovery must be attempted at most once.");
processState.FallBack("WebShell.ProcessFailure");
Require(processState.Availability == WebShellAvailability.NativeFallback, "Repeated process failure must select native fallback.");

Console.WriteLine("Web Shell smoke test passed.");
return;

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
file sealed class EchoHandler : IWebCommandHandler
{
    public string Command => "app.ping";
    public Task<object> HandleAsync(JsonElement payload, CancellationToken cancellationToken) => Task.FromResult<object>(new { pong = true });
}
