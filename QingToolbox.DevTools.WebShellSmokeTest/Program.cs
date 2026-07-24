using System.Security.Cryptography;
using System.Text.Json;
using QingToolbox.Shell.Startup;
using QingToolbox.Shell.WebShell;

var root = Directory.GetCurrentDirectory();
var dev = ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.Development, "WebShellSmoke", root);
Console.WriteLine("Verifying Web Shell environment and protocol v4 session semantics...");
Require(!new WebShellState(ApplicationExecutionEnvironment.Production()).IsEnvironmentAllowed, "Production must disable Web Shell.");
Require(new WebShellState(dev).IsEnvironmentAllowed, "Development must allow Web Shell.");
Require(!new WebShellState(ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.ModuleTest, "WebShellSmoke", root)).IsEnvironmentAllowed, "ModuleTest must disable Web Shell.");

var activation = new WebActivationSession();
using var generationOne = new CancellationTokenSource();
activation.Begin(1);
Reject(() => activation.RequireActivated(1, generationOne.Token), "BridgeNotActivated");
var nonce = activation.IssueChallenge(1, generationOne.Token);
Reject(() => activation.AcceptActivationPing(1, "wrong", generationOne.Token), "InvalidActivationNonce");
var sessionToken = activation.AcceptActivationPing(1, nonce, generationOne.Token);
Require(sessionToken.Length >= 64, "Activation must issue a 256-bit session token.");
activation.AcceptSessionPing(1, sessionToken, generationOne.Token);
activation.AcceptSessionPing(1, sessionToken, generationOne.Token);
Reject(() => activation.AcceptActivationPing(1, nonce, generationOne.Token), "InvalidBridgePhase");
Reject(() => activation.AcceptSessionPing(1, "wrong", generationOne.Token), "InvalidSessionToken");

activation.Begin(2);
using var generationTwo = new CancellationTokenSource();
Reject(() => activation.AcceptSessionPing(1, sessionToken, generationOne.Token), "StaleBridgeGeneration");
var nonceTwo = activation.IssueChallenge(2, generationTwo.Token);
generationTwo.Cancel();
Reject(() => activation.AcceptActivationPing(2, nonceTwo, generationTwo.Token), "BridgeSessionCancelled");
using var generationThree = new CancellationTokenSource();
activation.Begin(3);
var nonceThree = activation.IssueChallenge(3, generationThree.Token);
var tokenThree = activation.AcceptActivationPing(3, nonceThree, generationThree.Token);
activation.Invalidate(3);
Reject(() => activation.AcceptSessionPing(3, tokenThree, generationThree.Token), "BridgeNotActivated");

Console.WriteLine("Verifying stale handlers, generation cancellation and failure coalescing...");
var failures = new WebProcessFailureGate();
var coreOne = new object(); failures.Bind(coreOne, 1, generationOne.Token);
Require(!failures.TryAccept(new object(), 1, generationOne.Token, false), "A stale Core failure must be ignored.");
Require(failures.TryAccept(coreOne, 1, generationOne.Token, false), "The current generation failure must be accepted.");
Require(!failures.TryAccept(coreOne, 1, generationOne.Token, false), "A same-generation duplicate must be coalesced.");
var coreTwo = new object(); using var failureGenerationTwo = new CancellationTokenSource(); failures.Bind(coreTwo, 2, failureGenerationTwo.Token);
Require(!failures.TryAccept(coreOne, 1, generationOne.Token, false), "An old generation failure must remain ignored.");
Require(failures.TryAccept(coreTwo, 2, failureGenerationTwo.Token, false), "A recovered generation failure must be accepted.");
var recoveryState = new WebShellState(dev);
Require(recoveryState.TryBeginProcessRecovery() && !recoveryState.TryBeginProcessRecovery(), "Only one recovered generation is allowed.");
recoveryState.FallBack("WebShell.ProcessFailure.Repeated");
Require(recoveryState.Availability == WebShellAvailability.NativeFallback, "A second real generation failure must fall back.");
var sequence = new WebGenerationSequencer(); var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var secondEntered = false;
var firstGeneration = Task.Run(async () => { using var lease = await sequence.EnterAsync(CancellationToken.None); firstEntered.SetResult(); await releaseFirst.Task; });
await firstEntered.Task;
var secondGeneration = Task.Run(async () => { using var lease = await sequence.EnterAsync(CancellationToken.None); secondEntered = true; });
await Task.Delay(50); Require(!secondEntered, "A recovered handshake must not overlap the old handshake."); releaseFirst.SetResult(); await Task.WhenAll(firstGeneration, secondGeneration); Require(secondEntered, "Recovery must start after the old handshake exits.");

Console.WriteLine("Verifying dispatcher context and payload boundaries...");
var dispatcher = new WebBridgeDispatcher([new EchoHandler()]);
var requestId = Guid.NewGuid().ToString();
var context = new WebBridgeRequestContext(7, CancellationToken.None);
var success = await dispatcher.DispatchAsync(JsonSerializer.Serialize(new { protocolVersion = 4, requestId, command = "echo", payload = new { } }), context);
Require(success.Response.Success && success.ValidatedCommand == "echo", "A current typed context must dispatch.");
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
var cancelledResult = await dispatcher.DispatchAsync(JsonSerializer.Serialize(new { protocolVersion = 4, requestId, command = "echo", payload = new { } }), new(8, cancelled.Token));
Require(!cancelledResult.Response.Success && cancelledResult.Response.Error?.Code == "Cancelled", "A cancelled session must reject handler side effects.");
Require(!WebBridgeHost.TryPost(() => throw new ObjectDisposedException("core")), "Disposed Core posts must be isolated.");
Require(!WebBridgeHost.TryPost(() => throw new System.Runtime.InteropServices.COMException()), "Failed COM Core posts must be isolated.");

Console.WriteLine("Verifying immutable runtime assets, TOCTOU resistance and limits...");
var sourceAssets = Path.Combine(AppContext.BaseDirectory, "WebUI");
var valid = new WebAssetIdentity(sourceAssets);
Require(valid.Assets.ContainsKey("index.html") && !valid.TryResolve("/qing-web-assets.json", out _), "Only manifested output assets may be served.");
var originalIndex = valid.Assets["index.html"].Content.ToArray();
var temp = Path.Combine(Path.GetTempPath(), "QingToolbox-WebAssetSmoke-" + Guid.NewGuid().ToString("N")); CopyTree(sourceAssets, temp);
try
{
    var snapshot = new WebAssetIdentity(temp);
    var diskIndex = Path.Combine(temp, "index.html"); File.WriteAllText(diskIndex, "tampered after validation");
    Require(snapshot.Assets["index.html"].Content.Span.SequenceEqual(originalIndex), "A validated generation must retain original immutable bytes.");
    File.WriteAllBytes(diskIndex, originalIndex);
    File.WriteAllText(Path.Combine(temp, "extra.txt"), "extra"); RejectAsset(temp, "AssetFileSetMismatch");
}
finally { Directory.Delete(temp, true); }
Reject(() => WebAssetIdentity.RejectReparseAttributes(FileAttributes.ReparsePoint, "AssetFileReparsePoint"), "AssetFileReparsePoint");
Reject(() => WebAssetIdentity.RejectReparseAttributes(FileAttributes.Directory | FileAttributes.ReparsePoint, "AssetDirectoryReparsePoint"), "AssetDirectoryReparsePoint");
Reject(() => WebAssetIdentity.ValidateLimits(WebAssetIdentity.MaximumFileCount + 1, 0, 0), "AssetFileCountLimitExceeded");
Reject(() => WebAssetIdentity.ValidateLimits(1, WebAssetIdentity.MaximumFileBytes + 1, 0), "AssetFileSizeLimitExceeded");
Reject(() => WebAssetIdentity.ValidateLimits(1, 1, WebAssetIdentity.MaximumTotalBytes + 1), "AssetTotalSizeLimitExceeded");

Console.WriteLine("Verifying Development-only probe boundary...");
var probe = Guid.NewGuid();
var options = ApplicationLaunchOptions.Parse(["--environment", "Development", "--profile", "WebShellProbe", "--repo-root", root, "--web-shell-probe", probe.ToString()]);
Require(options.WebShellProbeId == probe, "Development must preserve probe identity.");
try { ApplicationLaunchOptions.Parse(["--environment", "Production", "--web-shell-probe", probe.ToString()]); throw new InvalidOperationException("Production accepted probe."); } catch (ArgumentException) { }
Console.WriteLine("Web Shell smoke test passed.");

static void CopyTree(string source, string target) { foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(directory.Replace(source, target)); foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) { var destination = file.Replace(source, target); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination); } }
static void RejectAsset(string path, string code) { try { _ = new WebAssetIdentity(path); throw new InvalidOperationException("Invalid asset tree was accepted."); } catch (WebAssetIdentityException exception) { Require(exception.Code == code, $"Expected {code}, received {exception.Code}."); } }
static void Reject(Action action, string code) { try { action(); throw new InvalidOperationException("Invalid operation was accepted."); } catch (WebBridgeValidationException exception) { Require(exception.Code == code, $"Expected {code}, received {exception.Code}."); } catch (WebAssetIdentityException exception) { Require(exception.Code == code, $"Expected {code}, received {exception.Code}."); } }
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
file sealed class EchoHandler : IWebCommandHandler
{
    public string Command => "echo";
    public IReadOnlySet<string> AllowedPayloadProperties { get; } = new HashSet<string>();
    public Task<object> HandleAsync(JsonElement payload, WebBridgeRequestContext context, CancellationToken token)
    { context.SessionCancellation.ThrowIfCancellationRequested(); return Task.FromResult<object>(new { generation = context.Generation }); }
}
