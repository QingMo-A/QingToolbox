using System.Security.Cryptography;
using System.Text.Json;
using QingToolbox.Core.Settings;
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
activation.Begin(1);
Reject(() => activation.AcceptSessionPing(1, sessionToken, generationOne.Token), "BridgeNotActivated");
var reloadNonce = activation.IssueChallenge(1, generationOne.Token);
var reloadToken = activation.AcceptActivationPing(1, reloadNonce, generationOne.Token);
Require(reloadToken != sessionToken, "A page reload must issue a fresh session token.");
activation.AcceptSessionPing(1, reloadToken, generationOne.Token);

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

Console.WriteLine("Verifying activated read-only module projection...");
var moduleActivation = new WebActivationSession();
moduleActivation.Begin(11);
var moduleNonce = moduleActivation.IssueChallenge(11, CancellationToken.None);
_ = moduleActivation.AcceptActivationPing(11, moduleNonce, CancellationToken.None);
var source = new SnapshotSource();
var moduleHandler = new WebModuleSnapshotCommandHandler(
    new WebModuleSnapshotProvider(source, TimeProvider.System), moduleActivation);
var moduleDispatcher = new WebBridgeDispatcher([moduleHandler]);
var moduleRequest = JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "modules.getSnapshot", payload = new { } });
var moduleResult = await moduleDispatcher.DispatchAsync(moduleRequest, new(11, CancellationToken.None));
Require(moduleResult.Response.Success && source.ReadCount == 1, "Activated module projection must read the existing source once.");
var projectedJson = JsonSerializer.Serialize(moduleResult.Response.Payload);
Require(!projectedJson.Contains("ModuleDirectory", StringComparison.OrdinalIgnoreCase) &&
        !projectedJson.Contains("ManifestPath", StringComparison.OrdinalIgnoreCase) &&
        !projectedJson.Contains("Entry", StringComparison.OrdinalIgnoreCase) &&
        !projectedJson.Contains(root, StringComparison.OrdinalIgnoreCase), "Module DTO must not expose execution paths.");
var beforeActivation = new WebActivationSession(); beforeActivation.Begin(12);
var rejected = await new WebBridgeDispatcher([new WebModuleSnapshotCommandHandler(
    new WebModuleSnapshotProvider(new SnapshotSource(), TimeProvider.System), beforeActivation)])
    .DispatchAsync(moduleRequest, new(12, CancellationToken.None));
Require(!rejected.Response.Success && rejected.Response.Error?.Code == "BridgeNotActivated", "Module projection must require an activated session.");
var extraPayload = JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "modules.getSnapshot", payload = new { path = root } });
var extraResult = await moduleDispatcher.DispatchAsync(extraPayload, new(11, CancellationToken.None));
Require(!extraResult.Response.Success && extraResult.Response.Error?.Code == "InvalidPayload", "Module projection must reject additional payload properties.");
var moduleSnapshot = (WebModuleSnapshot)moduleResult.Response.Payload;
Require(moduleSnapshot.Modules[0] is { CanLoad: false, CanActivate: false, CanOpen: true, CanDeactivate: true, CanUnload: true, IsBusy: false, IsExecutionBlocked: false, IsStartupEnabled: false, StartupAuthorizationState: "ChangedNeedsConfirmation", CanChangeStartupAuthorization: true, IsStartupAuthorizationBusy: false }, "Module projection must include host lifecycle and startup authorization capabilities.");

Console.WriteLine("Verifying explicit host-confirmed module lifecycle commands...");
var lifecycle = new LifecycleOperations();
var lifecycleSource = new SnapshotSource();
var lifecycleProvider = new WebModuleSnapshotProvider(lifecycleSource, TimeProvider.System);
var lifecycleDispatcher = new WebBridgeDispatcher([
    new WebModuleLoadCommandHandler(lifecycle, lifecycleProvider, moduleActivation),
    new WebModuleActivateCommandHandler(lifecycle, lifecycleProvider, moduleActivation),
    new WebModuleOpenCommandHandler(lifecycle, lifecycleProvider, moduleActivation),
    new WebModuleDeactivateCommandHandler(lifecycle, lifecycleProvider, moduleActivation),
    new WebModuleUnloadCommandHandler(lifecycle, lifecycleProvider, moduleActivation)]);
string LifecycleRequest(string command, object payload) => JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command, payload });
var inactiveLifecycle = new WebActivationSession(); inactiveLifecycle.Begin(13);
var inactiveLifecycleResult = await new WebBridgeDispatcher([new WebModuleLoadCommandHandler(lifecycle, lifecycleProvider, inactiveLifecycle)])
    .DispatchAsync(LifecycleRequest("modules.load", new { moduleId = "qing.test" }), new(13, CancellationToken.None));
Require(!inactiveLifecycleResult.Response.Success && inactiveLifecycleResult.Response.Error?.Code == "BridgeNotActivated", "Lifecycle commands must require activation.");
var inactiveOpenResult = await new WebBridgeDispatcher([new WebModuleOpenCommandHandler(lifecycle, lifecycleProvider, inactiveLifecycle)])
    .DispatchAsync(LifecycleRequest("modules.open", new { moduleId = "qing.test" }), new(13, CancellationToken.None));
Require(!inactiveOpenResult.Response.Success && inactiveOpenResult.Response.Error?.Code == "BridgeNotActivated", "Open must require activation.");
foreach (var command in new[] { "modules.deactivate", "modules.unload" })
{
    IWebCommandHandler handler = command == "modules.deactivate"
        ? new WebModuleDeactivateCommandHandler(lifecycle, lifecycleProvider, inactiveLifecycle)
        : new WebModuleUnloadCommandHandler(lifecycle, lifecycleProvider, inactiveLifecycle);
    var inactive = await new WebBridgeDispatcher([handler]).DispatchAsync(LifecycleRequest(command, new { moduleId = "qing.test" }), new(13, CancellationToken.None));
    Require(!inactive.Response.Success && inactive.Response.Error?.Code == "BridgeNotActivated", $"{command} must require activation.");
}
foreach (var payload in new object[] { new { }, new { moduleId = 42 }, new { moduleId = "" }, new { moduleId = "qing.test", path = root } })
{
    foreach (var command in new[] { "modules.load", "modules.open", "modules.deactivate", "modules.unload" })
    {
        var malformed = await lifecycleDispatcher.DispatchAsync(LifecycleRequest(command, payload), new(11, CancellationToken.None));
        Require(!malformed.Response.Success && malformed.Response.Error?.Code == "InvalidPayload", "Lifecycle commands must reject malformed payloads.");
    }
}
foreach (var outcome in new[] { WebModuleLifecycleResult.NotFound, WebModuleLifecycleResult.Busy, WebModuleLifecycleResult.Unavailable, WebModuleLifecycleResult.ExecutionBlocked, WebModuleLifecycleResult.Failed })
{
    lifecycle.NextResult = outcome;
    var failed = await lifecycleDispatcher.DispatchAsync(LifecycleRequest("modules.open", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
    var expected = outcome switch { WebModuleLifecycleResult.NotFound => "ModuleNotFound", WebModuleLifecycleResult.Busy => "ModuleBusy", WebModuleLifecycleResult.Unavailable => "ModuleOperationUnavailable", WebModuleLifecycleResult.ExecutionBlocked => "ModuleExecutionBlocked", _ => "ModuleOperationFailed" };
    Require(!failed.Response.Success && failed.Response.Error?.Code == expected, $"Lifecycle result {outcome} must map to {expected}.");
    Require(!JsonSerializer.Serialize(failed.Response).Contains(root, StringComparison.OrdinalIgnoreCase), "Lifecycle failures must not expose paths or stack details.");
}
lifecycle.NextResult = WebModuleLifecycleResult.Succeeded;
var loaded = await lifecycleDispatcher.DispatchAsync(LifecycleRequest("modules.load", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
var activated = await lifecycleDispatcher.DispatchAsync(LifecycleRequest("modules.activate", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
var opened = await lifecycleDispatcher.DispatchAsync(LifecycleRequest("modules.open", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
var deactivated = await lifecycleDispatcher.DispatchAsync(LifecycleRequest("modules.deactivate", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
var unloaded = await lifecycleDispatcher.DispatchAsync(LifecycleRequest("modules.unload", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
Require(loaded.Response.Success && activated.Response.Success && opened.Response.Success && deactivated.Response.Success && unloaded.Response.Success && unloaded.Response.Payload is WebModuleSnapshot, "Successful lifecycle commands must return complete snapshots.");
Require(lifecycle.LoadCount == 1 && lifecycle.ActivateCount == 1 && lifecycle.OpenCount == 6 && lifecycle.DeactivateCount == 1 && lifecycle.UnloadCount == 1 && lifecycle.LastModuleId == "qing.test", "Each accepted lifecycle request must call only its explicit adapter once.");
Require(WebBridgeProtocol.Version == 4, "Lifecycle commands must preserve protocol version 4.");
var unsupportedOperations = new WebBridgeDispatcher([]);
foreach (var command in new[] { "modules.remove", "settings.setLaunchAtLogin", "startup.repair", "startup.test" })
{
    var unsupported = await unsupportedOperations.DispatchAsync(LifecycleRequest(command, new { moduleId = "qing.test" }), new(11, CancellationToken.None));
    Require(!unsupported.Response.Success && unsupported.Response.Error?.Code == "UnknownCommand", "UI-3D must not register unrelated module or startup commands.");
}

Console.WriteLine("Verifying host-confirmed module startup authorization...");
var startupOperations = new StartupAuthorizationOperations();
var startupDispatcher = new WebBridgeDispatcher([new WebSetModuleStartupAuthorizationCommandHandler(startupOperations, lifecycleProvider, moduleActivation)]);
var inactiveStartup = new WebActivationSession(); inactiveStartup.Begin(14);
var inactiveStartupResult = await new WebBridgeDispatcher([new WebSetModuleStartupAuthorizationCommandHandler(startupOperations, lifecycleProvider, inactiveStartup)])
    .DispatchAsync(LifecycleRequest("modules.setStartupAuthorization", new { moduleId = "qing.test", enabled = true }), new(14, CancellationToken.None));
Require(!inactiveStartupResult.Response.Success && inactiveStartupResult.Response.Error?.Code == "BridgeNotActivated", "Startup authorization must require activation.");
foreach (var payload in new object[] { new { }, new { moduleId = "qing.test" }, new { moduleId = 42, enabled = true }, new { moduleId = "", enabled = true }, new { moduleId = "qing.test", enabled = "true" }, new { moduleId = "qing.test", enabled = true, path = root } })
{
    var malformed = await startupDispatcher.DispatchAsync(LifecycleRequest("modules.setStartupAuthorization", payload), new(11, CancellationToken.None));
    Require(!malformed.Response.Success && malformed.Response.Error?.Code == "InvalidPayload", "Startup authorization must reject malformed payloads.");
}
foreach (var outcome in new[] { WebModuleLifecycleResult.NotFound, WebModuleLifecycleResult.Busy, WebModuleLifecycleResult.Unavailable, WebModuleLifecycleResult.ExecutionBlocked, WebModuleLifecycleResult.Failed })
{
    startupOperations.NextResult = outcome;
    var failed = await startupDispatcher.DispatchAsync(LifecycleRequest("modules.setStartupAuthorization", new { moduleId = "qing.test", enabled = true }), new(11, CancellationToken.None));
    var expected = outcome switch { WebModuleLifecycleResult.NotFound => "ModuleNotFound", WebModuleLifecycleResult.Busy => "ModuleBusy", WebModuleLifecycleResult.Unavailable => "ModuleOperationUnavailable", WebModuleLifecycleResult.ExecutionBlocked => "ModuleExecutionBlocked", _ => "ModuleOperationFailed" };
    Require(!failed.Response.Success && failed.Response.Error?.Code == expected, $"Startup authorization result {outcome} must map safely.");
    var json = JsonSerializer.Serialize(failed.Response);
    Require(!json.Contains(root, StringComparison.OrdinalIgnoreCase) && !json.Contains("Sha256", StringComparison.OrdinalIgnoreCase), "Startup authorization failures must not expose paths or fingerprints.");
}
startupOperations.NextResult = WebModuleLifecycleResult.Succeeded;
var enabledStartup = await startupDispatcher.DispatchAsync(LifecycleRequest("modules.setStartupAuthorization", new { moduleId = "qing.test", enabled = true }), new(11, CancellationToken.None));
var disabledStartup = await startupDispatcher.DispatchAsync(LifecycleRequest("modules.setStartupAuthorization", new { moduleId = "qing.test", enabled = false }), new(11, CancellationToken.None));
Require(enabledStartup.Response.Success && disabledStartup.Response.Success && disabledStartup.Response.Payload is WebModuleSnapshot, "Startup authorization must return complete snapshots.");
Require(startupOperations.CallCount == 7 && !startupOperations.LastEnabled && startupOperations.LastModuleId == "qing.test", "Startup authorization must call its narrow adapter once per accepted request.");
Require(WebBridgeProtocol.Version == 4, "Startup authorization must preserve protocol version 4.");

Console.WriteLine("Verifying activated read-only session log projection...");
var logActivation = new WebActivationSession(); logActivation.Begin(21);
var logNonce = logActivation.IssueChallenge(21, CancellationToken.None);
_ = logActivation.AcceptActivationPing(21, logNonce, CancellationToken.None);
var logSource = new LogSnapshotSource();
var logDispatcher = new WebBridgeDispatcher([new WebLogSnapshotCommandHandler(new WebLogSnapshotProvider(logSource, TimeProvider.System), logActivation)]);
var logRequest = JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "logs.getSnapshot", payload = new { } });
var logResult = await logDispatcher.DispatchAsync(logRequest, new(21, CancellationToken.None));
Require(logResult.Response.Success && logSource.ReadCount == 1, "Log projection must read the in-memory source exactly once.");
var logSnapshot = (WebLogSnapshot)logResult.Response.Payload;
Require(logSnapshot.Entries.Count == 500 && logSnapshot.Entries[0].Message == "entry-1" && logSnapshot.Entries[^1].Message == "entry-500", "Log projection must retain the latest 500 entries in source order.");
var logJson = JsonSerializer.Serialize(logSnapshot);
Require(!logJson.Contains("Path", StringComparison.OrdinalIgnoreCase) && !logJson.Contains(root, StringComparison.OrdinalIgnoreCase), "Log DTO must not expose paths or file metadata.");
var inactiveLogs = new WebActivationSession(); inactiveLogs.Begin(22);
var inactiveLogResult = await new WebBridgeDispatcher([new WebLogSnapshotCommandHandler(new WebLogSnapshotProvider(new LogSnapshotSource(), TimeProvider.System), inactiveLogs)]).DispatchAsync(logRequest, new(22, CancellationToken.None));
Require(!inactiveLogResult.Response.Success && inactiveLogResult.Response.Error?.Code == "BridgeNotActivated", "Log projection must require an activated session.");
var extraLogPayload = JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "logs.getSnapshot", payload = new { file = "session.log" } });
var extraLogResult = await logDispatcher.DispatchAsync(extraLogPayload, new(21, CancellationToken.None));
Require(!extraLogResult.Response.Success && extraLogResult.Response.Error?.Code == "InvalidPayload", "Log projection must reject additional payload properties.");

Console.WriteLine("Verifying activated read-only settings projection...");
var settingsActivation = new WebActivationSession(); settingsActivation.Begin(31);
var settingsNonce = settingsActivation.IssueChallenge(31, CancellationToken.None);
_ = settingsActivation.AcceptActivationPing(31, settingsNonce, CancellationToken.None);
var settingsSource = new SettingsSnapshotSource();
var settingsDispatcher = new WebBridgeDispatcher([new WebSettingsSnapshotCommandHandler(new WebSettingsSnapshotProvider(settingsSource, TimeProvider.System), settingsActivation)]);
var settingsRequest = JsonSerializer.Serialize(new { protocolVersion = WebBridgeProtocol.Version, requestId = Guid.NewGuid(), command = "settings.getSnapshot", payload = new { } });
var settingsResult = await settingsDispatcher.DispatchAsync(settingsRequest, new(31, CancellationToken.None));
Require(settingsResult.Response.Success && settingsSource.ReadCount == 1, "Settings projection must read its authoritative source once.");
var settingsSnapshot = (WebSettingsSnapshot)settingsResult.Response.Payload;
Require(settingsSnapshot.Language.Code == "en-US" && settingsSnapshot.Language.DisplayName == "English", "Settings DTO must include the current language.");
Require(!settingsSnapshot.ShowLogsInSidebar && settingsSnapshot.MainWindowCloseBehavior == "Ask", "Settings DTO must include navigation and close behavior.");
Require(settingsSnapshot.LaunchAtLogin && settingsSnapshot.CanConfigureLaunchAtLogin && settingsSnapshot.StartupPresentationMode == "FloatingBadge" && settingsSnapshot.StartupBackend == "Task Scheduler" && settingsSnapshot.StartupStatus == "Healthy", "Settings DTO must include safe startup state.");
Require(settingsSource.WriteCount == 0, "Reading settings must not invoke a write operation.");
var settingsJson = JsonSerializer.Serialize(settingsSnapshot);
Require(!settingsJson.Contains("RegistryPath", StringComparison.OrdinalIgnoreCase) && !settingsJson.Contains("ExecutablePath", StringComparison.OrdinalIgnoreCase) && !settingsJson.Contains(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase), "Settings DTO must not expose system or user paths.");
var inactiveSettings = new WebActivationSession(); inactiveSettings.Begin(32);
var inactiveSettingsResult = await new WebBridgeDispatcher([new WebSettingsSnapshotCommandHandler(new WebSettingsSnapshotProvider(new SettingsSnapshotSource(), TimeProvider.System), inactiveSettings)]).DispatchAsync(settingsRequest, new(32, CancellationToken.None));
Require(!inactiveSettingsResult.Response.Success && inactiveSettingsResult.Response.Error?.Code == "BridgeNotActivated", "Settings projection must require an activated session.");
var extraSettingsPayload = JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "settings.getSnapshot", payload = new { save = true } });
var extraSettingsResult = await settingsDispatcher.DispatchAsync(extraSettingsPayload, new(31, CancellationToken.None));
Require(!extraSettingsResult.Response.Success && extraSettingsResult.Response.Error?.Code == "InvalidPayload", "Settings projection must reject additional payload properties.");
Require(WebBridgeProtocol.Version == 4, "Settings projection must preserve protocol version 4.");

Console.WriteLine("Verifying the single safe settings mutation...");
var mutationSource = new SettingsMutationSource(false);
var mutationDispatcher = new WebBridgeDispatcher([new WebSetShowLogsInSidebarCommandHandler(
    mutationSource, new WebSettingsSnapshotProvider(mutationSource, TimeProvider.System), settingsActivation)]);
string MutationRequest(object payload) => JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "settings.setShowLogsInSidebar", payload });
var missingMutation = await mutationDispatcher.DispatchAsync(MutationRequest(new { }), new(31, CancellationToken.None));
Require(!missingMutation.Response.Success && missingMutation.Response.Error?.Code == "InvalidPayload", "Settings mutation must require its Boolean field.");
var wrongMutation = await mutationDispatcher.DispatchAsync(MutationRequest(new { showLogsInSidebar = "true" }), new(31, CancellationToken.None));
Require(!wrongMutation.Response.Success && wrongMutation.Response.Error?.Code == "InvalidPayload", "Settings mutation must reject non-Boolean values.");
var extraMutation = await mutationDispatcher.DispatchAsync(MutationRequest(new { showLogsInSidebar = true, language = "zh-CN" }), new(31, CancellationToken.None));
Require(!extraMutation.Response.Success && extraMutation.Response.Error?.Code == "InvalidPayload", "Settings mutation must reject additional fields.");
var inactiveMutation = await new WebBridgeDispatcher([new WebSetShowLogsInSidebarCommandHandler(
    mutationSource, new WebSettingsSnapshotProvider(mutationSource, TimeProvider.System), inactiveSettings)])
    .DispatchAsync(MutationRequest(new { showLogsInSidebar = true }), new(32, CancellationToken.None));
Require(!inactiveMutation.Response.Success && inactiveMutation.Response.Error?.Code == "BridgeNotActivated", "Settings mutation must require activation.");
var enableMutation = await mutationDispatcher.DispatchAsync(MutationRequest(new { showLogsInSidebar = true }), new(31, CancellationToken.None));
Require(enableMutation.Response.Success && mutationSource.ShowLogsInSidebar && mutationSource.WriteCount == 1 && ((WebSettingsSnapshot)enableMutation.Response.Payload).ShowLogsInSidebar, "Settings mutation must persist false to true and return the new snapshot.");
var unchangedLanguage = ((WebSettingsSnapshot)enableMutation.Response.Payload).Language.Code;
var disableMutation = await mutationDispatcher.DispatchAsync(MutationRequest(new { showLogsInSidebar = false }), new(31, CancellationToken.None));
Require(disableMutation.Response.Success && !mutationSource.ShowLogsInSidebar && mutationSource.WriteCount == 2 && unchangedLanguage == "en-US", "Settings mutation must persist true to false without changing other settings.");
_ = await mutationDispatcher.DispatchAsync(MutationRequest(new { showLogsInSidebar = false }), new(31, CancellationToken.None));
Require(mutationSource.WriteCount == 2, "An unchanged settings value must not be persisted again.");
mutationSource.FailWrites = true;
var failedMutation = await mutationDispatcher.DispatchAsync(MutationRequest(new { showLogsInSidebar = true }), new(31, CancellationToken.None));
Require(!failedMutation.Response.Success && failedMutation.Response.Error?.Code == "HandlerFailed" && !mutationSource.ShowLogsInSidebar, "A failed settings write must preserve the authoritative value and return a safe error.");
Require(!JsonSerializer.Serialize(failedMutation.Response).Contains(root, StringComparison.OrdinalIgnoreCase), "A failed settings write must not expose paths or stack details.");

Console.WriteLine("Verifying the close behavior settings mutation...");
mutationSource.FailWrites = false;
var closeDispatcher = new WebBridgeDispatcher([new WebSetMainWindowCloseBehaviorCommandHandler(
    mutationSource, new WebSettingsSnapshotProvider(mutationSource, TimeProvider.System), settingsActivation)]);
string CloseRequest(object payload) => JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "settings.setMainWindowCloseBehavior", payload });
foreach (var payload in new object[] { new { }, new { mainWindowCloseBehavior = 1 }, new { mainWindowCloseBehavior = "Unknown" }, new { mainWindowCloseBehavior = "ask" }, new { mainWindowCloseBehavior = "" }, new { mainWindowCloseBehavior = "Ask", extra = true } })
{
    var rejectedClosePayload = await closeDispatcher.DispatchAsync(CloseRequest(payload), new(31, CancellationToken.None));
    Require(!rejectedClosePayload.Response.Success && rejectedClosePayload.Response.Error?.Code == "InvalidPayload", "Close behavior mutation must reject malformed or ambiguous payloads.");
}
var inactiveClose = await new WebBridgeDispatcher([new WebSetMainWindowCloseBehaviorCommandHandler(
    mutationSource, new WebSettingsSnapshotProvider(mutationSource, TimeProvider.System), inactiveSettings)])
    .DispatchAsync(CloseRequest(new { mainWindowCloseBehavior = "MinimizeToNotificationArea" }), new(32, CancellationToken.None));
Require(!inactiveClose.Response.Success && inactiveClose.Response.Error?.Code == "BridgeNotActivated", "Close behavior mutation must require activation.");
foreach (var expected in new[] { MainWindowCloseBehavior.MinimizeToNotificationArea, MainWindowCloseBehavior.ExitApplication, MainWindowCloseBehavior.Ask })
{
    var changed = await closeDispatcher.DispatchAsync(CloseRequest(new { mainWindowCloseBehavior = expected.ToString() }), new(31, CancellationToken.None));
    Require(changed.Response.Success && mutationSource.MainWindowCloseBehavior == expected && ((WebSettingsSnapshot)changed.Response.Payload).MainWindowCloseBehavior == expected.ToString(), "Close behavior mutation must persist and return each supported value.");
}
Require(mutationSource.CloseWriteCount == 3 && !mutationSource.ShowLogsInSidebar && mutationSource.Read().LaunchAtLogin, "Close behavior mutation must preserve logs and startup settings.");
_ = await closeDispatcher.DispatchAsync(CloseRequest(new { mainWindowCloseBehavior = "Ask" }), new(31, CancellationToken.None));
Require(mutationSource.CloseWriteCount == 3, "An unchanged close behavior must not be persisted again.");
mutationSource.FailWrites = true;
var failedClose = await closeDispatcher.DispatchAsync(CloseRequest(new { mainWindowCloseBehavior = "ExitApplication" }), new(31, CancellationToken.None));
Require(!failedClose.Response.Success && failedClose.Response.Error?.Code == "HandlerFailed" && mutationSource.MainWindowCloseBehavior == MainWindowCloseBehavior.Ask, "A failed close behavior write must preserve authority and return a safe error.");
Require(!JsonSerializer.Serialize(failedClose.Response).Contains(root, StringComparison.OrdinalIgnoreCase) && WebBridgeProtocol.Version == 4, "Close behavior failure must not expose paths and protocol version must remain 4.");

Console.WriteLine("Verifying the startup presentation settings mutation...");
mutationSource.FailWrites = false;
var presentationDispatcher = new WebBridgeDispatcher([new WebSetStartupPresentationModeCommandHandler(
    mutationSource, new WebSettingsSnapshotProvider(mutationSource, TimeProvider.System), settingsActivation)]);
string PresentationRequest(object payload) => JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "settings.setStartupPresentationMode", payload });
foreach (var payload in new object[] { new { }, new { startupPresentationMode = 1 }, new { startupPresentationMode = "" }, new { startupPresentationMode = "Unknown" }, new { startupPresentationMode = "floatingbadge" }, new { startupPresentationMode = "MainWindow", extra = true } })
{
    var rejectedPresentation = await presentationDispatcher.DispatchAsync(PresentationRequest(payload), new(31, CancellationToken.None));
    Require(!rejectedPresentation.Response.Success && rejectedPresentation.Response.Error?.Code == "InvalidPayload", "Startup presentation mutation must reject malformed or ambiguous payloads.");
}
var inactivePresentation = await new WebBridgeDispatcher([new WebSetStartupPresentationModeCommandHandler(
    mutationSource, new WebSettingsSnapshotProvider(mutationSource, TimeProvider.System), inactiveSettings)])
    .DispatchAsync(PresentationRequest(new { startupPresentationMode = "MainWindow" }), new(32, CancellationToken.None));
Require(!inactivePresentation.Response.Success && inactivePresentation.Response.Error?.Code == "BridgeNotActivated", "Startup presentation mutation must require activation.");
foreach (var expected in new[] { StartupPresentationMode.MainWindow, StartupPresentationMode.Minimized, StartupPresentationMode.FloatingBadge })
{
    var changed = await presentationDispatcher.DispatchAsync(PresentationRequest(new { startupPresentationMode = expected.ToString() }), new(31, CancellationToken.None));
    Require(changed.Response.Success && mutationSource.StartupPresentationMode == expected && ((WebSettingsSnapshot)changed.Response.Payload).StartupPresentationMode == expected.ToString(), "Startup presentation mutation must persist and return each supported value.");
}
var preservedSettings = mutationSource.Read();
Require(mutationSource.PresentationWriteCount == 3 && !mutationSource.ShowLogsInSidebar && mutationSource.MainWindowCloseBehavior == MainWindowCloseBehavior.Ask && preservedSettings.LaunchAtLogin, "Startup presentation mutation must preserve logs, close behavior and launch settings.");
_ = await presentationDispatcher.DispatchAsync(PresentationRequest(new { startupPresentationMode = "FloatingBadge" }), new(31, CancellationToken.None));
Require(mutationSource.PresentationWriteCount == 3, "An unchanged startup presentation must not be persisted again.");
mutationSource.FailWrites = true;
var failedPresentation = await presentationDispatcher.DispatchAsync(PresentationRequest(new { startupPresentationMode = "MainWindow" }), new(31, CancellationToken.None));
Require(!failedPresentation.Response.Success && failedPresentation.Response.Error?.Code == "HandlerFailed" && mutationSource.StartupPresentationMode == StartupPresentationMode.FloatingBadge, "A failed startup presentation write must preserve authority and return a safe error.");
Require(!JsonSerializer.Serialize(failedPresentation.Response).Contains(root, StringComparison.OrdinalIgnoreCase) && WebBridgeProtocol.Version == 4, "Startup presentation failure must not expose paths and protocol version must remain 4.");

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
file sealed class SnapshotSource : IWebModuleSnapshotSource
{
    public int ReadCount { get; private set; }
    public IReadOnlyList<WebModuleSnapshotItem> ReadModules()
    {
        ReadCount++;
        return [new("qing.test", "Test", "Safe description", "1.0.0", "Qing", "OutOfProcess", "Manual",
            "Running", true, 0, [], ["Clipboard"], "0.2.0-alpha", true, false, false, true, true, true, false, false,
            false, "ChangedNeedsConfirmation", true, false)];
    }
}
file sealed class LifecycleOperations : IWebModuleLifecycleOperations
{
    public WebModuleLifecycleResult NextResult { get; set; } = WebModuleLifecycleResult.Succeeded;
    public int LoadCount { get; private set; }
    public int ActivateCount { get; private set; }
    public int OpenCount { get; private set; }
    public int DeactivateCount { get; private set; }
    public int UnloadCount { get; private set; }
    public string LastModuleId { get; private set; } = string.Empty;
    public Task<WebModuleLifecycleResult> LoadAsync(string moduleId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); LoadCount++; LastModuleId = moduleId; return Task.FromResult(NextResult); }
    public Task<WebModuleLifecycleResult> ActivateAsync(string moduleId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); ActivateCount++; LastModuleId = moduleId; return Task.FromResult(NextResult); }
    public Task<WebModuleLifecycleResult> OpenAsync(string moduleId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); OpenCount++; LastModuleId = moduleId; return Task.FromResult(NextResult); }
    public Task<WebModuleLifecycleResult> DeactivateAsync(string moduleId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); DeactivateCount++; LastModuleId = moduleId; return Task.FromResult(NextResult); }
    public Task<WebModuleLifecycleResult> UnloadAsync(string moduleId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); UnloadCount++; LastModuleId = moduleId; return Task.FromResult(NextResult); }
}
file sealed class StartupAuthorizationOperations : IWebModuleStartupAuthorizationOperations
{
    public WebModuleLifecycleResult NextResult { get; set; } = WebModuleLifecycleResult.Succeeded;
    public int CallCount { get; private set; }
    public string LastModuleId { get; private set; } = string.Empty;
    public bool LastEnabled { get; private set; }
    public Task<WebModuleLifecycleResult> SetAsync(string moduleId, bool enabled, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); CallCount++; LastModuleId = moduleId; LastEnabled = enabled; return Task.FromResult(NextResult); }
}
file sealed class LogSnapshotSource : IWebLogSnapshotSource
{
    public int ReadCount { get; private set; }
    public IReadOnlyList<WebLogSnapshotEntry> ReadEntries()
    {
        ReadCount++;
        return Enumerable.Range(0, 501).Select(index => new WebLogSnapshotEntry(
            DateTimeOffset.UnixEpoch.AddSeconds(index), (index % 3) switch { 0 => "Information", 1 => "Warning", _ => "Error" }, "Test", $"entry-{index}")).ToArray();
    }
}
file sealed class SettingsSnapshotSource : IWebSettingsSnapshotSource
{
    public int ReadCount { get; private set; }
    public int WriteCount { get; private set; }
    public WebSettingsSnapshotValues Read()
    {
        ReadCount++;
        return new(new("en-US", "English"), false, "Ask", "Ask before closing.", true, true,
            "FloatingBadge", "Task Scheduler", "Healthy", "Startup registration is healthy.");
    }
}
file sealed class SettingsMutationSource(bool initialValue) : IWebSettingsSnapshotSource, IWebSettingsMutation
{
    public bool ShowLogsInSidebar { get; private set; } = initialValue;
    public MainWindowCloseBehavior MainWindowCloseBehavior { get; private set; } = MainWindowCloseBehavior.Ask;
    public StartupPresentationMode StartupPresentationMode { get; private set; } = StartupPresentationMode.FloatingBadge;
    public bool FailWrites { get; set; }
    public int WriteCount { get; private set; }
    public int CloseWriteCount { get; private set; }
    public int PresentationWriteCount { get; private set; }
    public WebSettingsSnapshotValues Read() => new(new("en-US", "English"), ShowLogsInSidebar,
        MainWindowCloseBehavior.ToString(), "Ask before closing.", true, true, StartupPresentationMode.ToString(), "Task Scheduler", "Healthy", "Startup registration is healthy.");
    public Task SetShowLogsInSidebarAsync(bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrites) throw new IOException("Synthetic settings write failure.");
        WriteCount++;
        ShowLogsInSidebar = value;
        return Task.CompletedTask;
    }
    public Task SetMainWindowCloseBehaviorAsync(MainWindowCloseBehavior value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrites) throw new IOException("Synthetic settings write failure.");
        CloseWriteCount++;
        MainWindowCloseBehavior = value;
        return Task.CompletedTask;
    }
    public Task SetStartupPresentationModeAsync(StartupPresentationMode value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrites) throw new IOException("Synthetic settings write failure.");
        PresentationWriteCount++;
        StartupPresentationMode = value;
        return Task.CompletedTask;
    }
}
