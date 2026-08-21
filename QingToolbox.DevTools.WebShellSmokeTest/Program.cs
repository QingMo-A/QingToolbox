using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using QingToolbox.Core.Settings;
using QingToolbox.Core.Localization;
using QingToolbox.Core.Updates;
using QingToolbox.Shell.Services;
using QingToolbox.Shell.Startup;
using QingToolbox.Shell.WebShell;
using QingToolbox.Shell.Windowing;

var root = Directory.GetCurrentDirectory();
var dev = ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.Development, "WebShellSmoke", root);
var production = ApplicationExecutionEnvironment.Production();
var moduleTest = ApplicationExecutionEnvironment.Sandbox(
    ApplicationEnvironmentKind.ModuleTest, "WebShellSmoke", root);
Console.WriteLine("Verifying Web Shell environment and protocol v4 session semantics...");
Require(new WebShellState(production).IsEnvironmentAllowed, "Production must allow the verified Web Shell.");
Require(new WebShellState(dev).IsEnvironmentAllowed, "Development must allow Web Shell.");
Require(!new WebShellState(moduleTest).IsEnvironmentAllowed, "ModuleTest must disable Web Shell.");
Require(ModuleUpdateTransactionHostPolicy.SupportsTransactions(production) &&
        ModuleUpdateTransactionHostPolicy.SupportsTransactions(dev) &&
        ModuleUpdateTransactionHostPolicy.SupportsTransactions(moduleTest),
    "All isolated host environments must register the gated module transaction coordinator.");
Require(ModuleUpdateTransactionHostPolicy.SupportsWebInstall(production) &&
        ModuleUpdateTransactionHostPolicy.SupportsWebInstall(dev) &&
        !ModuleUpdateTransactionHostPolicy.SupportsWebInstall(moduleTest),
    "Production and Development must register the verified-update Web handler while ModuleTest remains native-only.");
var productionModuleUpdateServices = new ServiceCollection();
ModuleUpdateTransactionHostRegistration.AddTransactionServices(productionModuleUpdateServices, production);
ModuleUpdateTransactionHostRegistration.AddWebInstallServices(productionModuleUpdateServices, production);
Require(productionModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(ModuleUpdateTransactionService)) &&
        productionModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(GatedModuleUpdateTransactionCoordinator)) &&
        productionModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(IWebModuleUpdateInstallOperations)) &&
        productionModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(IWebCommandHandler) &&
            descriptor.ImplementationType == typeof(WebModuleInstallVerifiedUpdateCommandHandler)),
    "Production composition must register the transaction service, gated coordinator and verified-update Web handler.");
var developmentModuleUpdateServices = new ServiceCollection();
ModuleUpdateTransactionHostRegistration.AddTransactionServices(developmentModuleUpdateServices, dev);
ModuleUpdateTransactionHostRegistration.AddWebInstallServices(developmentModuleUpdateServices, dev);
Require(developmentModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(ModuleUpdateTransactionService)) &&
        developmentModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(GatedModuleUpdateTransactionCoordinator)) &&
        developmentModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(IWebModuleUpdateInstallOperations)) &&
        developmentModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(IWebCommandHandler) &&
            descriptor.ImplementationType == typeof(WebModuleInstallVerifiedUpdateCommandHandler)),
    "Development composition must preserve the transaction service, gated coordinator and verified-update Web handler.");
var moduleTestModuleUpdateServices = new ServiceCollection();
ModuleUpdateTransactionHostRegistration.AddTransactionServices(moduleTestModuleUpdateServices, moduleTest);
ModuleUpdateTransactionHostRegistration.AddWebInstallServices(moduleTestModuleUpdateServices, moduleTest);
Require(moduleTestModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(ModuleUpdateTransactionService)) &&
        moduleTestModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(GatedModuleUpdateTransactionCoordinator)) &&
        !moduleTestModuleUpdateServices.Any(descriptor =>
            descriptor.ServiceType == typeof(IWebModuleUpdateInstallOperations) ||
            descriptor.ServiceType == typeof(IWebCommandHandler)),
    "ModuleTest must retain transaction recovery support without exposing a Web install handler.");

Console.WriteLine("Verifying native Web workspace presentation transitions...");
Require(WindowChromeBehavior.GetDwmCornerPreference(WindowState.Normal) == WindowChromeBehavior.DwmWindowCornerPreferenceRound &&
        WindowChromeBehavior.GetDwmCornerPreference(WindowState.Maximized) == WindowChromeBehavior.DwmWindowCornerPreferenceDoNotRound,
    "Native windows must round in normal state and opt out of DWM rounding while maximized.");
var developmentPresentation = new WebWorkspacePresentationState(webShellAllowed: true);
Require(developmentPresentation.Phase == WebWorkspacePresentationPhase.Preparing &&
        developmentPresentation.Snapshot is { ShowNativeWorkspace: false, ShowStartupSurface: true, AttachWebWorkspace: true, EnableWebWorkspace: false },
    "Development must start on the native startup surface with an attached but hidden Web workspace.");
Require(developmentPresentation.TryPrepare(isExiting: false), "A live Development session must accept the preparing WebView.");
Require(developmentPresentation.TryShowReady(isExiting: false) &&
        developmentPresentation.Snapshot is { ShowNativeWorkspace: false, ShowStartupSurface: false, AttachWebWorkspace: true, EnableWebWorkspace: true },
    "Only the ready transition may expose and enable the Web workspace.");
developmentPresentation.ShowNativeFallback();
Require(developmentPresentation.Snapshot is { ShowNativeWorkspace: true, ShowStartupSurface: false, AttachWebWorkspace: false, EnableWebWorkspace: false },
    "A Web failure must restore only the native workspace.");

var slowReadyPresentation = new WebWorkspacePresentationState(webShellAllowed: true);
Require(slowReadyPresentation.TryPrepare(isExiting: false) && slowReadyPresentation.Phase == WebWorkspacePresentationPhase.Preparing,
    "A slow ready handshake must keep the startup surface visible.");
Require(!slowReadyPresentation.TryShowReady(isExiting: true) && slowReadyPresentation.Phase == WebWorkspacePresentationPhase.Preparing,
    "An exiting session must reject a delayed ready callback.");
var fastReadyPresentation = new WebWorkspacePresentationState(webShellAllowed: true);
Require(fastReadyPresentation.TryShowReady(isExiting: false) && fastReadyPresentation.Phase == WebWorkspacePresentationPhase.Ready,
    "A fast ready handshake must transition directly from the startup surface without exposing native content.");
var nativePresentation = new WebWorkspacePresentationState(webShellAllowed: false);
Require(nativePresentation.Phase == WebWorkspacePresentationPhase.Native &&
        !nativePresentation.TryPrepare(isExiting: false) && !nativePresentation.TryShowReady(isExiting: false),
    "ModuleTest presentation must remain native-only.");

Console.WriteLine("Verifying Restart Manager maintenance shutdown messages...");
Require(NativeWindowMessages.IsRestartManagerQuery(
        NativeWindowMessages.WindowQueryEndSession,
        new IntPtr(NativeWindowMessages.EndSessionCloseApplication)),
    "Restart Manager close queries must be approved without using the user's close-button behavior.");
Require(!NativeWindowMessages.IsRestartManagerShutdown(
        NativeWindowMessages.WindowEndSession,
        IntPtr.Zero,
        new IntPtr(NativeWindowMessages.EndSessionCloseApplication)),
    "A cancelled Restart Manager session must not exit the application.");
Require(NativeWindowMessages.IsRestartManagerShutdown(
        NativeWindowMessages.WindowEndSession,
        new IntPtr(1),
        new IntPtr(NativeWindowMessages.EndSessionCloseApplication)),
    "A confirmed Restart Manager maintenance shutdown must exit the application.");
Require(!NativeWindowMessages.IsRestartManagerQuery(
        NativeWindowMessages.WindowQueryEndSession,
        IntPtr.Zero),
    "Ordinary logoff or shutdown messages must retain the existing session-ending path.");

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
Require(WebShellThemeNotification.TryParse("{\"kind\":\"qing.ui.theme\",\"mode\":\"dark\"}", out var darkTheme) && darkTheme == WebShellThemeMode.Dark,
    "The native title bar must accept a bounded Web theme notification.");
foreach (var invalidTheme in new[] { "{}", "{\"kind\":\"qing.ui.theme\",\"mode\":\"invalid\"}", "{\"kind\":\"qing.ui.theme\",\"mode\":\"dark\",\"extra\":true}" })
    Require(!WebShellThemeNotification.TryParse(invalidTheme, out _), "Malformed or extended theme notifications must be rejected.");
var defaultLightTitleBar = WindowTitleBarThemeManager.Project(AppearancePresetIds.QingDefault, WebShellThemeMode.Light);
var defaultDarkTitleBar = WindowTitleBarThemeManager.Project(AppearancePresetIds.QingDefault, WebShellThemeMode.Dark);
Require(defaultLightTitleBar.Background != defaultDarkTitleBar.Background &&
        defaultLightTitleBar.Foreground != defaultDarkTitleBar.Foreground,
    "The native title bar must preserve the light/dark combination for qing-default.");
var titleBarPresets = new[]
{
    AppearancePresetIds.QingDefault, AppearancePresetIds.NeonCircuit, AppearancePresetIds.Greenline,
    AppearancePresetIds.AuroraFlow, AppearancePresetIds.QingNova
}.Select(id => WindowTitleBarThemeManager.Project(id, WebShellThemeMode.Light)).ToArray();
Require(titleBarPresets.Length == 5 && titleBarPresets.Select(item => item.Accent).Distinct(StringComparer.Ordinal).Count() == 5 &&
        titleBarPresets.All(item => !string.IsNullOrWhiteSpace(item.Background) && !string.IsNullOrWhiteSpace(item.Disabled)),
    "Every host appearance preset must project distinct native title-bar accent and safe disabled tokens.");
var titleBarFallback = WindowTitleBarThemeManager.Project("invalid-preset", WebShellThemeMode.Light);
Require(titleBarFallback == defaultLightTitleBar,
    "An invalid host appearance preset must fall back to the qing-default native palette.");

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
Require(moduleSnapshot.Modules[0] is { CanRemove: true, CanLoad: false, CanActivate: false, CanOpen: true, CanDeactivate: true, CanUnload: true, IsBusy: false, IsExecutionBlocked: false, IsStartupEnabled: false, StartupAuthorizationState: "ChangedNeedsConfirmation", CanChangeStartupAuthorization: true, IsStartupAuthorizationBusy: false }, "Module projection must include host management, lifecycle and startup authorization capabilities.");
Require(moduleSnapshot.Modules[0] is { UpdateStatus: "UpdateAvailable", TargetVersion: "1.1.0", ReleaseNotes: "Safe release notes", IsFromStaleCache: false, CanCheckForUpdate: true, IsUpdateCheckBusy: false, CanDownloadUpdate: true, DownloadStatus: "Verified", IsDownloadActive: false, DownloadBytesReceived: 512, DownloadExpectedBytes: 512, CanInstallVerifiedUpdate: true },
    "Module projection must include the host-authoritative update and verified staging state.");

Console.WriteLine("Verifying bounded module icon projection...");
var iconRoot = Path.Combine(Path.GetTempPath(), "QingToolbox-WebModuleIcon-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(iconRoot);
try
{
    var iconPath = Path.Combine(iconRoot, "icon.svg");
    var iconBytes = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" />");
    File.WriteAllBytes(iconPath, iconBytes);
    var iconData = WebModuleIconProjection.ReadDataUri(iconRoot, iconPath);
    Require(iconData is not null && iconData.StartsWith(WebModuleIconProjection.DataUriPrefix, StringComparison.Ordinal),
        "A valid manifest SVG must be projected as a data URI.");
    Require(Convert.FromBase64String(iconData![WebModuleIconProjection.DataUriPrefix.Length..]).SequenceEqual(iconBytes),
        "Projected icon bytes must remain unchanged.");
    Require(!iconData.Contains(iconRoot, StringComparison.OrdinalIgnoreCase),
        "Projected icon data must not expose the module directory.");
    Require(WebModuleIconProjection.ReadDataUri(iconRoot, Path.Combine(iconRoot, "missing.svg")) is null,
        "A missing icon must fall back to null.");

    var outsidePath = Path.Combine(Path.GetTempPath(), "QingToolbox-WebModuleIcon-outside-" + Guid.NewGuid().ToString("N") + ".svg");
    File.WriteAllBytes(outsidePath, iconBytes);
    try
    {
        Require(WebModuleIconProjection.ReadDataUri(iconRoot, outsidePath) is null,
            "An icon outside the module root must be rejected.");
    }
    finally { File.Delete(outsidePath); }

    var oversizedPath = Path.Combine(iconRoot, "oversized.svg");
    File.WriteAllBytes(oversizedPath, new byte[WebModuleIconProjection.MaximumIconBytes + 1]);
    Require(WebModuleIconProjection.ReadDataUri(iconRoot, oversizedPath) is null,
        "An oversized icon must fall back to null.");
    var unreadablePath = Path.Combine(iconRoot, "unreadable.svg");
    Directory.CreateDirectory(unreadablePath);
    Require(WebModuleIconProjection.ReadDataUri(iconRoot, unreadablePath) is null,
        "An unreadable icon path must fall back to null.");
}
finally { Directory.Delete(iconRoot, true); }

Console.WriteLine("Verifying module update bridge boundaries...");
var updateOperations = new UpdateOperations();
var updateDispatcher = new WebBridgeDispatcher([
    new WebModuleCheckUpdateCommandHandler(updateOperations, new WebModuleSnapshotProvider(source, TimeProvider.System), moduleActivation),
    new WebModuleDownloadUpdateCommandHandler(updateOperations, new WebModuleSnapshotProvider(source, TimeProvider.System), moduleActivation)]);
foreach (var command in new[] { "modules.checkUpdate", "modules.downloadUpdate" })
{
    foreach (var payload in new object[]
    {
        new { }, new { moduleId = 42 }, new { moduleId = "" }, new { moduleId = "qing.test", url = "https://invalid.example" },
        new { moduleId = "qing.test", targetVersion = "9.9.9" }, new { moduleId = "qing.test", sha256 = "00" },
        new { moduleId = "qing.test", size = 1 }, new { moduleId = "qing.test", stagingPath = root },
        new { moduleId = "qing.test", packagePath = root }
    })
    {
        var malformed = await updateDispatcher.DispatchAsync(LifecycleRequest(command, payload), new(11, CancellationToken.None));
        Require(!malformed.Response.Success && malformed.Response.Error?.Code == "InvalidPayload", $"{command} must accept only a non-empty moduleId.");
    }
}
var checkedUpdate = await updateDispatcher.DispatchAsync(LifecycleRequest("modules.checkUpdate", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
var downloadedUpdate = await updateDispatcher.DispatchAsync(LifecycleRequest("modules.downloadUpdate", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
Require(checkedUpdate.Response.Success && downloadedUpdate.Response.Success && downloadedUpdate.Response.Payload is WebModuleSnapshot { Modules.Count: 1 } &&
        updateOperations.CheckCount == 1 && updateOperations.DownloadCount == 1 && updateOperations.LastModuleId == "qing.test",
    "Update commands must use only the update adapter and return complete snapshots.");
updateOperations.NextResult = WebModuleUpdateOperationResult.Unavailable;
var unavailableUpdate = await updateDispatcher.DispatchAsync(LifecycleRequest("modules.downloadUpdate", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
Require(!unavailableUpdate.Response.Success && unavailableUpdate.Response.Error?.Code == "ModuleUpdateUnavailable" &&
        !JsonSerializer.Serialize(unavailableUpdate.Response).Contains(root, StringComparison.OrdinalIgnoreCase),
    "Unavailable downloads must return a safe error without host paths or download metadata.");
Require(WebBridgeProtocol.Version == 4, "Module update commands must preserve protocol version 4.");

Console.WriteLine("Verifying host-gated verified update installation...");
var installOperations = new InstallUpdateOperations();
var installDispatcher = new WebBridgeDispatcher([
    new WebModuleInstallVerifiedUpdateCommandHandler(installOperations,
        new WebModuleSnapshotProvider(source, TimeProvider.System), moduleActivation)]);
foreach (var payload in new object[]
{
    new { }, new { moduleId = 42 }, new { moduleId = "" },
    new { moduleId = "qing.test", packagePath = root }, new { moduleId = "qing.test", stagingPath = root },
    new { moduleId = "qing.test", targetVersion = "9.9.9" }, new { moduleId = "qing.test", sha256 = "00" },
    new { moduleId = "qing.test", url = "https://invalid.example" }
})
{
    var malformed = await installDispatcher.DispatchAsync(
        LifecycleRequest("modules.installVerifiedUpdate", payload), new(11, CancellationToken.None));
    Require(!malformed.Response.Success && malformed.Response.Error?.Code == "InvalidPayload",
        "Verified update installation must accept only a non-empty moduleId.");
}
var installedUpdate = await installDispatcher.DispatchAsync(
    LifecycleRequest("modules.installVerifiedUpdate", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
Require(installedUpdate.Response.Success && installedUpdate.Response.Payload is WebModuleUpdateInstallResponse
    { Disposition: "Installed", SourceVersion: "1.0.0", TargetVersion: "1.1.0", Snapshot.Modules.Count: 1 } &&
    installOperations.CallCount == 1 && installOperations.LastModuleId == "qing.test",
    "Verified update installation must use only its host adapter and return a complete snapshot.");
installOperations.NextResult = new(WebModuleUpdateInstallOperationStatus.Unavailable);
var unavailableInstall = await installDispatcher.DispatchAsync(
    LifecycleRequest("modules.installVerifiedUpdate", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
Require(!unavailableInstall.Response.Success && unavailableInstall.Response.Error?.Code == "ModuleUpdateUnavailable" &&
        !JsonSerializer.Serialize(unavailableInstall.Response).Contains(root, StringComparison.OrdinalIgnoreCase),
    "Ineligible verified packages must return a safe unavailable error.");

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

Console.WriteLine("Verifying native-picker module import bridge boundaries...");
var importOperations = new ImportOperations();
var importDispatcher = new WebBridgeDispatcher([
    new WebModuleImportCommandHandler(importOperations, lifecycleProvider, moduleActivation)]);
var cancelledImport = await importDispatcher.DispatchAsync(
    LifecycleRequest("modules.import", new { }), new(11, CancellationToken.None));
Require(cancelledImport.Response.Success && cancelledImport.Response.Payload is WebModuleImportResponse
    { Disposition: "Cancelled", ImportedModuleId: null, Snapshot.Modules.Count: 1 },
    "Cancelling the native picker must return a non-error result with a complete snapshot.");
importOperations.NextResult = new(WebModuleImportDisposition.Imported, "qing.imported");
var importedModule = await importDispatcher.DispatchAsync(
    LifecycleRequest("modules.import", new { }), new(11, CancellationToken.None));
Require(importedModule.Response.Success && importedModule.Response.Payload is WebModuleImportResponse
    { Disposition: "Imported", ImportedModuleId: "qing.imported", Snapshot.Modules.Count: 1 } && importOperations.CallCount == 2,
    "A successful native import must return the imported ID and complete authoritative snapshot.");
var importWithPath = await importDispatcher.DispatchAsync(
    LifecycleRequest("modules.import", new { packagePath = root }), new(11, CancellationToken.None));
Require(!importWithPath.Response.Success && importWithPath.Response.Error?.Code == "InvalidPayload" && importOperations.CallCount == 2,
    "Web module import must reject frontend-supplied paths before invoking the adapter.");
importOperations.Failure = new IOException($"Synthetic failure at {root}");
var failedImport = await importDispatcher.DispatchAsync(
    LifecycleRequest("modules.import", new { }), new(11, CancellationToken.None));
Require(!failedImport.Response.Success && failedImport.Response.Error?.Code == "HandlerFailed" &&
        !JsonSerializer.Serialize(failedImport.Response).Contains(root, StringComparison.OrdinalIgnoreCase),
    "Import failures must use the existing safe bridge error without exposing local paths.");
Require(WebBridgeProtocol.Version == 4, "Module import must preserve protocol version 4.");

Console.WriteLine("Verifying safe module management bridge boundaries...");
var managementOperations = new ManagementOperations();
var managementDispatcher = new WebBridgeDispatcher([
    new WebModuleOpenDirectoryCommandHandler(managementOperations, lifecycleProvider, moduleActivation),
    new WebModuleRemoveCommandHandler(managementOperations, lifecycleProvider, moduleActivation)]);
foreach (var command in new[] { "modules.openDirectory", "modules.remove" })
{
    foreach (var payload in new object[] { new { }, new { moduleId = 42 }, new { moduleId = "" }, new { moduleId = "qing.test", directoryPath = root } })
    {
        var malformed = await managementDispatcher.DispatchAsync(LifecycleRequest(command, payload), new(11, CancellationToken.None));
        Require(!malformed.Response.Success && malformed.Response.Error?.Code == "InvalidPayload", $"{command} must accept only a non-empty moduleId.");
    }
}
var openedDirectory = await managementDispatcher.DispatchAsync(LifecycleRequest("modules.openDirectory", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
Require(openedDirectory.Response.Success && openedDirectory.Response.Payload is WebModuleManagementResponse { Disposition: "Succeeded", Snapshot.Modules.Count: 1 } && managementOperations.OpenCount == 1,
    "Opening a module directory must use the management adapter and return no path.");
managementOperations.NextResult = WebModuleManagementResult.SucceededWithWarning;
var removedModule = await managementDispatcher.DispatchAsync(LifecycleRequest("modules.remove", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
Require(removedModule.Response.Success && removedModule.Response.Payload is WebModuleManagementResponse { Disposition: "SucceededWithWarning", Snapshot.Modules.Count: 1 } && managementOperations.RemoveCount == 1,
    "Module removal must preserve partial-success semantics with a complete snapshot.");
var managementJson = JsonSerializer.Serialize(new[] { openedDirectory.Response, removedModule.Response });
Require(!managementJson.Contains(root, StringComparison.OrdinalIgnoreCase) && !managementJson.Contains("DirectoryPath", StringComparison.OrdinalIgnoreCase),
    "Module management responses must not expose local paths.");
managementOperations.NextResult = WebModuleManagementResult.Unavailable;
var unavailableRemoval = await managementDispatcher.DispatchAsync(LifecycleRequest("modules.remove", new { moduleId = "qing.builtin" }), new(11, CancellationToken.None));
Require(!unavailableRemoval.Response.Success && unavailableRemoval.Response.Error?.Code == "ModuleOperationUnavailable",
    "A host-rejected built-in removal must remain unavailable through Web.");
foreach (var (outcome, code) in new[]
{
    (WebModuleManagementResult.NotFound, "ModuleNotFound"),
    (WebModuleManagementResult.Busy, "ModuleBusy"),
    (WebModuleManagementResult.ExecutionBlocked, "ModuleExecutionBlocked"),
    (WebModuleManagementResult.Failed, "ModuleOperationFailed")
})
{
    managementOperations.NextResult = outcome;
    var failed = await managementDispatcher.DispatchAsync(LifecycleRequest("modules.remove", new { moduleId = "qing.test" }), new(11, CancellationToken.None));
    Require(!failed.Response.Success && failed.Response.Error?.Code == code &&
            !JsonSerializer.Serialize(failed.Response).Contains(root, StringComparison.OrdinalIgnoreCase),
        $"Management result {outcome} must map to the safe {code} error.");
}
Require(WebBridgeProtocol.Version == 4, "Module management must preserve protocol version 4.");

var unsupportedOperations = new WebBridgeDispatcher([]);
foreach (var command in new[] { "startup.repair", "startup.test" })
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
Require(settingsSnapshot.Language.Code == "en-US" && settingsSnapshot.Language.EffectiveCode == "en-US" &&
        settingsSnapshot.Language.DisplayName == "English", "Settings DTO must include configured and effective language state.");
Require(settingsSnapshot.Language.Options.Count == 3 &&
        settingsSnapshot.Language.Options.Select(option => option.Code).Distinct(StringComparer.Ordinal).Count() == 3 &&
        settingsSnapshot.Language.Options.Select(option => option.Code).SequenceEqual(["system", "zh-CN", "en-US"]),
    "Settings DTO must include every unique supported language option.");
Require(!settingsSnapshot.ShowLogsInSidebar && settingsSnapshot.MainWindowCloseBehavior == "Ask", "Settings DTO must include navigation and close behavior.");
Require(settingsSnapshot.AppearancePresetId == AppearancePresetIds.QingDefault,
    "Settings DTO must include the host-confirmed appearance preset.");
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

Console.WriteLine("Verifying font settings schema and bridge boundaries...");
var legacyFontSettingsPath = Path.Combine(Path.GetTempPath(), "QingToolbox-font-settings-" + Guid.NewGuid().ToString("N") + ".json");
try
{
    File.WriteAllText(legacyFontSettingsPath,
        "{\"SettingsSchemaVersion\":8,\"FontSource\":\"imported\",\"FontId\":\"imported:not-a-sha\",\"FontFamilyName\":\"unsafe\\\\family\"}");
    using var legacyFontSettings = new UserSettingsService(legacyFontSettingsPath);
    var normalizedLegacy = await legacyFontSettings.ReadAsync();
    Require(normalizedLegacy.SettingsSchemaVersion == 9 && normalizedLegacy.FontId == FontPreferenceIds.Default &&
            normalizedLegacy.FontSource == FontPreferenceSources.Default && normalizedLegacy.FontFamilyName is null,
        "Legacy or malformed font settings must normalize to schema 9 Default.");
}
finally { try { File.Delete(legacyFontSettingsPath); } catch { } }

var fontRouteSettingsPath = Path.Combine(Path.GetTempPath(), "QingToolbox-font-route-" + Guid.NewGuid().ToString("N") + ".json");
using (var fontRouteSettings = new UserSettingsService(fontRouteSettingsPath))
{
    var fontPaths = new ApplicationPaths(dev);
    fontPaths.EnsureDirectories();
    var fontService = new FontSettingsService(fontPaths, fontRouteSettings);
    var validHash = new string('a', 64);
    Require(!fontService.TryOpenWebResource("/user-fonts/../" + validHash + ".ttf", out _, out _),
        "Font resource route must reject path traversal.");
    Require(!fontService.TryOpenWebResource("/user-fonts/" + validHash + ".ttf/extra", out _, out _),
        "Font resource route must reject nested route leaves.");
    Require(!fontService.TryOpenWebResource("/user-fonts/" + validHash + ".ttf", out _, out _),
        "Font resource route must reject a missing controlled copy.");
    Require(!fontService.TryOpenWebResource("/user-fonts/not-a-hash.ttf", out _, out _),
        "Font resource route must reject malformed hashes.");

    var systemFontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    var sourceFont = Directory.EnumerateFiles(systemFontsDirectory, "*.ttf").FirstOrDefault();
    Require(sourceFont is not null, "Windows must expose at least one installed TTF for the font import smoke test.");
    var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFont!))).ToLowerInvariant();
    var controlledCopy = Path.Combine(fontPaths.ImportedFontsDirectory,
        $"font-{sourceHash}{Path.GetExtension(sourceFont!).ToLowerInvariant()}");
    var controlledCopyExisted = File.Exists(controlledCopy);
    try
    {
        var import = await fontService.ImportFromPathAsync(sourceFont!);
        var importedSelection = import.Selection;
        Require(import.Disposition == FontSettingsService.FontImportDisposition.Imported &&
                importedSelection is { Source: FontPreferenceSources.Imported, FilePath: not null } &&
                File.Exists(importedSelection.FilePath),
            $"A valid installed font file must be copied into the environment-scoped managed font directory; disposition={import.Disposition}.");
        var resourcePath = new Uri(importedSelection!.ResourceUrl!, UriKind.Absolute).AbsolutePath;
        Require(fontService.TryOpenWebResource(resourcePath, out var fontContent, out var fontContentType) &&
                fontContent is not null && fontContent.Length > 0 && fontContentType == "font/ttf",
            "An imported font must be readable only through its verified same-origin resource route.");
        fontContent?.Dispose();
        var persisted = await fontRouteSettings.ReadAsync();
        Require(persisted.FontId == importedSelection.Id && persisted.FontSource == FontPreferenceSources.Imported,
            "A successful import must persist only the opaque imported font id and safe family metadata.");
        Require((await fontService.SetAsync(FontPreferenceIds.Default)).Disposition ==
                FontSettingsService.FontSelectionDisposition.Succeeded,
            "The managed font catalog must always allow restoring the built-in Default.");
    }
    finally
    {
        if (!controlledCopyExisted)
        {
            try { File.Delete(controlledCopy); } catch { }
        }
    }
}
try { File.Delete(fontRouteSettingsPath); } catch { }

Console.WriteLine("Verifying persistent system font cache and explicit refresh semantics...");
var cacheEnvironment = ApplicationExecutionEnvironment.Sandbox(ApplicationEnvironmentKind.Development, "FontCacheSmoke", root);
var cachePaths = new ApplicationPaths(cacheEnvironment);
cachePaths.EnsureDirectories();
try { File.Delete(cachePaths.FontCatalogCachePath); } catch { }
var cacheEnumerationCount = 0;
IReadOnlyList<string> FirstEnumeration()
{
    Interlocked.Increment(ref cacheEnumerationCount);
    return ["Cache Sans", "Cache Mono"];
}
var cacheSettingsPathA = Path.Combine(cachePaths.RoamingRoot, "cache-a-settings.json");
var cacheSettingsPathB = Path.Combine(cachePaths.RoamingRoot, "cache-b-settings.json");
var cacheSettingsPathC = Path.Combine(cachePaths.RoamingRoot, "cache-c-settings.json");
using (var cacheSettingsA = new UserSettingsService(cacheSettingsPathA))
{
    var firstService = new FontSettingsService(cachePaths, cacheSettingsA, FirstEnumeration);
    var firstSnapshot = firstService.CreateSnapshot();
    Require(cacheEnumerationCount == 1 && firstSnapshot.Options.Any(item => item.Id == "system:Cache Sans") &&
            File.Exists(cachePaths.FontCatalogCachePath),
        "The first font catalog load must enumerate once and persist a versioned cache.");
    var cacheJson = File.ReadAllText(cachePaths.FontCatalogCachePath);
    Require(cacheJson.Contains("\"schemaVersion\":1", StringComparison.Ordinal) &&
            cacheJson.Contains("Cache Sans", StringComparison.Ordinal),
        "The system font cache must include its schema version and validated family names.");
}
using (var cacheSettingsB = new UserSettingsService(cacheSettingsPathB))
{
    var cacheHitService = new FontSettingsService(cachePaths, cacheSettingsB, () =>
    {
        Interlocked.Increment(ref cacheEnumerationCount);
        return ["Should Not Enumerate"];
    });
    var cacheHitSnapshot = cacheHitService.CreateSnapshot();
    Require(cacheEnumerationCount == 1 && cacheHitSnapshot.Options.Any(item => item.Id == "system:Cache Sans") &&
            !cacheHitSnapshot.Options.Any(item => item.Id == "system:Should Not Enumerate"),
        "A second process must use the environment-scoped system font cache without enumerating.");
}
File.WriteAllText(cachePaths.FontCatalogCachePath, "{ this is not valid json");
using (var cacheSettingsC = new UserSettingsService(cacheSettingsPathC))
{
    var recoveryService = new FontSettingsService(cachePaths, cacheSettingsC, () =>
    {
        Interlocked.Increment(ref cacheEnumerationCount);
        return ["Recovered Sans"];
    });
    var recoveredSnapshot = recoveryService.CreateSnapshot();
    Require(cacheEnumerationCount == 2 && recoveredSnapshot.Options.Any(item => item.Id == "system:Recovered Sans"),
        "A corrupt cache must be ignored safely and rebuilt from one enumeration.");
    var refreshed = await recoveryService.RefreshSystemFontsAsync();
    Require(cacheEnumerationCount == 3 && refreshed.Options.Any(item => item.Id == "system:Recovered Sans"),
        "Only explicit refresh must force a second system font enumeration.");

    var sourceFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
    var sourceFontForCache = Directory.Exists(sourceFonts)
        ? Directory.EnumerateFiles(sourceFonts, "*.ttf").FirstOrDefault()
        : null;
    if (sourceFontForCache is not null)
    {
        var sourceHashForCache = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sourceFontForCache))).ToLowerInvariant();
        var managedCopyForCache = Path.Combine(cachePaths.ImportedFontsDirectory,
            $"font-{sourceHashForCache}{Path.GetExtension(sourceFontForCache).ToLowerInvariant()}");
        var managedCopyExistedForCache = File.Exists(managedCopyForCache);
        try
        {
            var importedWithoutRescan = await recoveryService.ImportFromPathAsync(sourceFontForCache);
            Require(importedWithoutRescan.Disposition == FontSettingsService.FontImportDisposition.Imported &&
                    cacheEnumerationCount == 3,
                "Importing a font must update the imported group without invalidating the system cache.");
        }
        finally
        {
            if (!managedCopyExistedForCache)
            {
                try { File.Delete(managedCopyForCache); } catch { }
            }
        }
    }
}
try { File.Delete(cacheSettingsPathA); } catch { }
try { File.Delete(cacheSettingsPathB); } catch { }
try { File.Delete(cacheSettingsPathC); } catch { }

var fontSnapshotSource = new FontSnapshotSource();
var fontOperations = new FontOperations();
var fontSnapshotProvider = new WebSettingsSnapshotProvider(new SettingsSnapshotSource(), TimeProvider.System, fontSnapshotSource);
var fontDispatcher = new WebBridgeDispatcher([
    new WebSetFontCommandHandler(fontOperations, fontSnapshotProvider, settingsActivation),
    new WebImportFontCommandHandler(fontOperations, fontSnapshotProvider, settingsActivation),
    new WebRefreshFontsCommandHandler(fontOperations, fontSnapshotProvider, settingsActivation)]);
string FontRequest(string command, object payload) => JsonSerializer.Serialize(new
{
    protocolVersion = WebBridgeProtocol.Version,
    requestId = Guid.NewGuid(), command, payload
});
var malformedFont = await fontDispatcher.DispatchAsync(FontRequest("settings.setFont", new { fontId = "../private" }), new(31, CancellationToken.None));
Require(!malformedFont.Response.Success && malformedFont.Response.Error?.Code == "InvalidPayload",
    "Font selection must reject an unsafe or overlong id before invoking the adapter.");
var selectedFont = await fontDispatcher.DispatchAsync(FontRequest("settings.setFont", new { fontId = "system:Segoe UI" }), new(31, CancellationToken.None));
Require(selectedFont.Response.Success && fontOperations.SetCount == 1 && selectedFont.Response.Payload is WebSettingsSnapshot { Font: not null },
    "Font selection must return a complete host-confirmed snapshot.");
var selectedFontJson = JsonSerializer.Serialize(selectedFont.Response.Payload);
Require(!selectedFontJson.Contains("FilePath", StringComparison.OrdinalIgnoreCase) &&
        !selectedFontJson.Contains(root, StringComparison.OrdinalIgnoreCase),
    "Font snapshots must not expose local paths.");
fontOperations.ImportResult = WebFontMutationResult.Cancelled;
var cancelledFontImport = await fontDispatcher.DispatchAsync(FontRequest("settings.importFont", new { }), new(31, CancellationToken.None));
Require(cancelledFontImport.Response.Success && cancelledFontImport.Response.Payload is WebFontImportResponse { Disposition: "Cancelled" },
    "Cancelling native font import must return an explicit non-error disposition.");
fontOperations.ImportResult = WebFontMutationResult.Succeeded;
var importedFont = await fontDispatcher.DispatchAsync(FontRequest("settings.importFont", new { }), new(31, CancellationToken.None));
Require(importedFont.Response.Success && importedFont.Response.Payload is WebFontImportResponse { Disposition: "Imported" } && fontOperations.ImportCount == 2,
    "Successful native font import must return an explicit Imported disposition.");
var refreshedFonts = await fontDispatcher.DispatchAsync(FontRequest("settings.refreshFonts", new { }), new(31, CancellationToken.None));
Require(refreshedFonts.Response.Success && refreshedFonts.Response.Payload is WebSettingsSnapshot && fontOperations.RefreshCount == 1,
    "Explicit font refresh must invoke the host refresh operation and return a complete snapshot.");
var malformedFontRefresh = await fontDispatcher.DispatchAsync(FontRequest("settings.refreshFonts", new { extra = true }), new(31, CancellationToken.None));
Require(!malformedFontRefresh.Response.Success && malformedFontRefresh.Response.Error?.Code == "InvalidPayload",
    "Font refresh must reject additional payload properties.");
Require(WebBridgeProtocol.Version == 4, "Font settings commands must preserve protocol v4.");

Console.WriteLine("Verifying host-confirmed language mutation...");
var languageSource = new SettingsMutationSource(false);
var languageDispatcher = new WebBridgeDispatcher([new WebSetLanguageCommandHandler(
    languageSource, new WebSettingsSnapshotProvider(languageSource, TimeProvider.System), settingsActivation)]);
string LanguageRequest(object payload) => JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "settings.setLanguage", payload });
foreach (var payload in new object[] { new { }, new { languageCode = 1 }, new { languageCode = "" }, new { languageCode = "zh-cn" }, new { languageCode = "EN-US" }, new { languageCode = "fr-FR" }, new { languageCode = "en-US", extra = true } })
{
    var rejectedLanguage = await languageDispatcher.DispatchAsync(LanguageRequest(payload), new(31, CancellationToken.None));
    Require(!rejectedLanguage.Response.Success && rejectedLanguage.Response.Error?.Code == "InvalidPayload", "Language mutation must reject malformed, unsupported or ambiguous payloads.");
}
var inactiveLanguage = await new WebBridgeDispatcher([new WebSetLanguageCommandHandler(languageSource,
    new WebSettingsSnapshotProvider(languageSource, TimeProvider.System), inactiveSettings)])
    .DispatchAsync(LanguageRequest(new { languageCode = "zh-CN" }), new(32, CancellationToken.None));
Require(!inactiveLanguage.Response.Success && inactiveLanguage.Response.Error?.Code == "BridgeNotActivated", "Language mutation must require activation.");
foreach (var languageCode in new[] { "system", "zh-CN", "en-US" })
{
    var changed = await languageDispatcher.DispatchAsync(LanguageRequest(new { languageCode }), new(31, CancellationToken.None));
    var changedSnapshot = (WebSettingsSnapshot)changed.Response.Payload;
    Require(changed.Response.Success && languageSource.LanguageCode == languageCode && changedSnapshot.Language.Code == languageCode &&
            changedSnapshot.ShowLogsInSidebar == languageSource.ShowLogsInSidebar && changedSnapshot.MainWindowCloseBehavior == languageSource.MainWindowCloseBehavior.ToString(),
        "Language mutation must return a complete host-confirmed snapshot without changing other settings.");
}
Require(languageSource.LanguageWriteCount == 3, "Each changed supported language must be persisted once.");
_ = await languageDispatcher.DispatchAsync(LanguageRequest(new { languageCode = "en-US" }), new(31, CancellationToken.None));
Require(languageSource.LanguageWriteCount == 3, "An unchanged language request must not write again.");
languageSource.FailWrites = true;
var failedLanguage = await languageDispatcher.DispatchAsync(LanguageRequest(new { languageCode = "zh-CN" }), new(31, CancellationToken.None));
var failedLanguageJson = JsonSerializer.Serialize(failedLanguage.Response);
Require(!failedLanguage.Response.Success && failedLanguage.Response.Error?.Code == "SettingsMutationFailed" && languageSource.LanguageCode == "en-US",
    "A failed language mutation must preserve the previous confirmed language.");
Require(!failedLanguageJson.Contains(root, StringComparison.OrdinalIgnoreCase) && !failedLanguageJson.Contains("IOException", StringComparison.OrdinalIgnoreCase) &&
        WebBridgeProtocol.Version == 4, "Language failures must remain safe and preserve protocol v4.");

Console.WriteLine("Verifying host-confirmed appearance preset mutation...");
var appearanceSource = new SettingsMutationSource(false);
var appearanceDispatcher = new WebBridgeDispatcher([new WebSetAppearancePresetCommandHandler(
    appearanceSource, new WebSettingsSnapshotProvider(appearanceSource, TimeProvider.System), settingsActivation)]);
string AppearanceRequest(object payload) => JsonSerializer.Serialize(new
{
    protocolVersion = 4,
    requestId = Guid.NewGuid(),
    command = "settings.setAppearancePreset",
    payload
});
foreach (var payload in new object[]
         {
             new { }, new { appearancePresetId = 1 }, new { appearancePresetId = "" },
             new { appearancePresetId = "Qing-Default" }, new { appearancePresetId = "unknown" },
             new { appearancePresetId = AppearancePresetIds.QingDefault, extra = true }
         })
{
    var rejectedAppearance = await appearanceDispatcher.DispatchAsync(AppearanceRequest(payload), new(31, CancellationToken.None));
    Require(!rejectedAppearance.Response.Success && rejectedAppearance.Response.Error?.Code == "InvalidPayload",
        "Appearance preset mutation must reject malformed, unknown or ambiguous payloads.");
}
var inactiveAppearance = await new WebBridgeDispatcher([new WebSetAppearancePresetCommandHandler(
        appearanceSource, new WebSettingsSnapshotProvider(appearanceSource, TimeProvider.System), inactiveSettings)])
    .DispatchAsync(AppearanceRequest(new { appearancePresetId = AppearancePresetIds.QingNova }),
        new(32, CancellationToken.None));
Require(!inactiveAppearance.Response.Success && inactiveAppearance.Response.Error?.Code == "BridgeNotActivated",
    "Appearance preset mutation must require activation.");
foreach (var presetId in new[]
         {
             AppearancePresetIds.NeonCircuit, AppearancePresetIds.Greenline,
             AppearancePresetIds.AuroraFlow, AppearancePresetIds.QingNova,
             AppearancePresetIds.QingDefault
         })
{
    var changed = await appearanceDispatcher.DispatchAsync(
        AppearanceRequest(new { appearancePresetId = presetId }), new(31, CancellationToken.None));
    Require(changed.Response.Success && appearanceSource.AppearancePresetId == presetId &&
            ((WebSettingsSnapshot)changed.Response.Payload).AppearancePresetId == presetId,
        "Each supported appearance preset must return a complete host-confirmed snapshot.");
}
Require(appearanceSource.AppearanceWriteCount == 5,
    "Each changed appearance preset must be persisted exactly once.");
_ = await appearanceDispatcher.DispatchAsync(
    AppearanceRequest(new { appearancePresetId = AppearancePresetIds.QingDefault }), new(31, CancellationToken.None));
Require(appearanceSource.AppearanceWriteCount == 5,
    "An unchanged appearance preset must not be persisted again.");
appearanceSource.FailWrites = true;
var failedAppearance = await appearanceDispatcher.DispatchAsync(
    AppearanceRequest(new { appearancePresetId = AppearancePresetIds.AuroraFlow }),
    new(31, CancellationToken.None));
Require(!failedAppearance.Response.Success && failedAppearance.Response.Error?.Code == "SettingsMutationFailed" &&
        appearanceSource.AppearancePresetId == AppearancePresetIds.QingDefault,
    "A failed appearance preset mutation must preserve the previous confirmed preset.");
Require(WebBridgeProtocol.Version == 4,
    "Appearance preset settings must preserve protocol v4.");

Console.WriteLine("Verifying transactional LocalizationManager language persistence...");
var languageTransactionRoot = Path.Combine(Path.GetTempPath(), "QingToolbox-LanguageSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(languageTransactionRoot);
try
{
    var settingsPath = Path.Combine(languageTransactionRoot, "settings.json");
    using var settingsService = new UserSettingsService(settingsPath);
    var localizationManager = new LocalizationManager(settingsService);
    await localizationManager.InitializeAsync(languageTransactionRoot, "en-US");
    var cultureChanges = 0;
    localizationManager.CultureChanged += (_, _) => cultureChanges++;

    await localizationManager.SetLanguageAsync("en-US");
    Require(!File.Exists(settingsPath) && cultureChanges == 0, "An unchanged configured and effective language must not write or raise an event.");

    await localizationManager.SetLanguageAsync("zh-CN");
    var persisted = await settingsService.ReadAsync();
    Require(persisted.Language == "zh-CN" && localizationManager.ConfiguredLanguageCode == "zh-CN" &&
            localizationManager.CurrentLanguageCode == "zh-CN" && cultureChanges == 1,
        "A successful language save must commit persistence before in-memory language state.");

    var blockingParent = Path.Combine(languageTransactionRoot, "blocking-parent");
    await File.WriteAllTextAsync(blockingParent, "not a directory");
    using var failingSettings = new UserSettingsService(Path.Combine(blockingParent, "settings.json"));
    var failingManager = new LocalizationManager(failingSettings);
    await failingManager.InitializeAsync(languageTransactionRoot, "en-US");
    var failedEvents = 0;
    failingManager.CultureChanged += (_, _) => failedEvents++;
    try { await failingManager.SetLanguageAsync("zh-CN"); throw new InvalidOperationException("Expected deterministic settings failure."); }
    catch (IOException) { }
    Require(failingManager.ConfiguredLanguageCode == "en-US" && failingManager.CurrentLanguageCode == "en-US" && failedEvents == 0,
        "A failed language save must preserve configured and effective state without raising an event.");

    using var languageCancellation = new CancellationTokenSource();
    languageCancellation.Cancel();
    try { await failingManager.SetLanguageAsync("zh-CN", languageCancellation.Token); throw new InvalidOperationException("Expected language cancellation."); }
    catch (OperationCanceledException) { }
    Require(failingManager.ConfiguredLanguageCode == "en-US" && failingManager.CurrentLanguageCode == "en-US" && failedEvents == 0,
        "A cancelled language save must preserve configured and effective state.");

    await localizationManager.SetLanguageAsync("en-US");
}
finally
{
    Directory.Delete(languageTransactionRoot, recursive: true);
}

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

Console.WriteLine("Verifying host-confirmed Launch at login mutation...");
mutationSource.FailWrites = false;
var launchDispatcher = new WebBridgeDispatcher([new WebSetLaunchAtLoginCommandHandler(
    mutationSource, new WebSettingsSnapshotProvider(mutationSource, TimeProvider.System), settingsActivation)]);
string LaunchRequest(object payload) => JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "settings.setLaunchAtLogin", payload });
foreach (var payload in new object[] { new { }, new { enabled = "true" }, new { enabled = true, extra = true } })
{
    var rejectedLaunch = await launchDispatcher.DispatchAsync(LaunchRequest(payload), new(31, CancellationToken.None));
    Require(!rejectedLaunch.Response.Success && rejectedLaunch.Response.Error?.Code == "InvalidPayload", "Launch at login must reject malformed payloads.");
}
var inactiveLaunch = await new WebBridgeDispatcher([new WebSetLaunchAtLoginCommandHandler(mutationSource,
    new WebSettingsSnapshotProvider(mutationSource, TimeProvider.System), inactiveSettings)])
    .DispatchAsync(LaunchRequest(new { enabled = false }), new(32, CancellationToken.None));
Require(!inactiveLaunch.Response.Success && inactiveLaunch.Response.Error?.Code == "BridgeNotActivated", "Launch at login must require activation.");
var launchDisabled = await launchDispatcher.DispatchAsync(LaunchRequest(new { enabled = false }), new(31, CancellationToken.None));
var launchEnabled = await launchDispatcher.DispatchAsync(LaunchRequest(new { enabled = true }), new(31, CancellationToken.None));
Require(launchDisabled.Response.Success && launchEnabled.Response.Success && mutationSource.LaunchWriteCount == 2 &&
    ((WebSettingsSnapshot)launchEnabled.Response.Payload).LaunchAtLogin, "Launch at login must call its adapter once and return a full confirmed snapshot.");
_ = await launchDispatcher.DispatchAsync(LaunchRequest(new { enabled = true }), new(31, CancellationToken.None));
Require(mutationSource.LaunchWriteCount == 2, "Unchanged Launch at login requests must not write again.");
mutationSource.CanConfigureLaunchAtLogin = false;
var unavailableLaunch = await launchDispatcher.DispatchAsync(LaunchRequest(new { enabled = false }), new(31, CancellationToken.None));
Require(!unavailableLaunch.Response.Success && unavailableLaunch.Response.Error?.Code == "SettingsMutationUnavailable", "Unavailable startup registration must map safely.");
mutationSource.CanConfigureLaunchAtLogin = true; mutationSource.FailWrites = true;
var failedLaunch = await launchDispatcher.DispatchAsync(LaunchRequest(new { enabled = false }), new(31, CancellationToken.None));
var failedLaunchJson = JsonSerializer.Serialize(failedLaunch.Response);
Require(!failedLaunch.Response.Success && failedLaunch.Response.Error?.Code == "SettingsMutationFailed" && mutationSource.LaunchAtLogin, "Failed startup writes must preserve confirmed state.");
Require(!failedLaunchJson.Contains(root, StringComparison.OrdinalIgnoreCase) && !failedLaunchJson.Contains("Registry", StringComparison.OrdinalIgnoreCase) && WebBridgeProtocol.Version == 4, "Launch failures must not expose paths and must preserve protocol v4.");

Console.WriteLine("Verifying host-confirmed startup registration repair...");
mutationSource.FailWrites = false; mutationSource.CanRepairStartup = true;
var repairDispatcher = new WebBridgeDispatcher([new WebRepairStartupRegistrationCommandHandler(
    mutationSource, new WebSettingsSnapshotProvider(mutationSource, TimeProvider.System), settingsActivation)]);
string RepairRequest(object payload) => JsonSerializer.Serialize(new { protocolVersion = 4, requestId = Guid.NewGuid(), command = "settings.repairStartupRegistration", payload });
var inactiveRepair = await new WebBridgeDispatcher([new WebRepairStartupRegistrationCommandHandler(mutationSource,
    new WebSettingsSnapshotProvider(mutationSource, TimeProvider.System), inactiveSettings)])
    .DispatchAsync(RepairRequest(new { }), new(32, CancellationToken.None));
Require(!inactiveRepair.Response.Success && inactiveRepair.Response.Error?.Code == "BridgeNotActivated", "Startup repair must require activation.");
var invalidRepair = await repairDispatcher.DispatchAsync(RepairRequest(new { extra = true }), new(31, CancellationToken.None));
Require(!invalidRepair.Response.Success && invalidRepair.Response.Error?.Code == "InvalidPayload", "Startup repair must reject payload properties.");
var repaired = await repairDispatcher.DispatchAsync(RepairRequest(new { }), new(31, CancellationToken.None));
Require(repaired.Response.Success && mutationSource.RepairWriteCount == 1 && !((WebSettingsSnapshot)repaired.Response.Payload).CanRepairStartup,
    "Startup repair must call its adapter once and return a confirmed full snapshot.");
var unavailableRepair = await repairDispatcher.DispatchAsync(RepairRequest(new { }), new(31, CancellationToken.None));
Require(!unavailableRepair.Response.Success && unavailableRepair.Response.Error?.Code == "SettingsMutationUnavailable", "Healthy startup state must not remain repairable.");
mutationSource.CanRepairStartup = true; mutationSource.RepairReturnsDisabled = true;
var cleanupRepair = await repairDispatcher.DispatchAsync(RepairRequest(new { }), new(31, CancellationToken.None));
Require(cleanupRepair.Response.Success && !((WebSettingsSnapshot)cleanupRepair.Response.Payload).LaunchAtLogin && mutationSource.RepairWriteCount == 2,
    "Cleanup repair may succeed with startup disabled.");
mutationSource.CanRepairStartup = true; mutationSource.FailWrites = true;
var failedRepair = await repairDispatcher.DispatchAsync(RepairRequest(new { }), new(31, CancellationToken.None));
var failedRepairJson = JsonSerializer.Serialize(failedRepair.Response);
Require(!failedRepair.Response.Success && failedRepair.Response.Error?.Code == "SettingsMutationFailed" && mutationSource.CanRepairStartup,
    "Failed startup repair must preserve the previous repairable state.");
Require(!failedRepairJson.Contains(root, StringComparison.OrdinalIgnoreCase) && !failedRepairJson.Contains("HKCU", StringComparison.OrdinalIgnoreCase) &&
    !failedRepairJson.Contains("stack", StringComparison.OrdinalIgnoreCase) && WebBridgeProtocol.Version == 4,
    "Startup repair failures must remain safe and preserve protocol v4.");

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
            "Running", true, 0, [], ["Clipboard"], "0.2.0-alpha", true, true, false, false, true, true, true, false, false,
            false, "ChangedNeedsConfirmation", true, false, "UpdateAvailable", "1.1.0", "Safe release notes", false,
            true, false, true, "Verified", false, 512, 512, true)];
    }
}
file sealed class UpdateOperations : IWebModuleUpdateOperations
{
    public WebModuleUpdateOperationResult NextResult { get; set; } = WebModuleUpdateOperationResult.Succeeded;
    public int CheckCount { get; private set; }
    public int DownloadCount { get; private set; }
    public string LastModuleId { get; private set; } = string.Empty;
    public Task<WebModuleUpdateOperationResult> CheckAsync(string moduleId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); CheckCount++; LastModuleId = moduleId; return Task.FromResult(NextResult); }
    public Task<WebModuleUpdateOperationResult> DownloadAndVerifyAsync(string moduleId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); DownloadCount++; LastModuleId = moduleId; return Task.FromResult(NextResult); }
}
file sealed class InstallUpdateOperations : IWebModuleUpdateInstallOperations
{
    public WebModuleUpdateInstallOperationResult NextResult { get; set; } =
        new(WebModuleUpdateInstallOperationStatus.Installed, "1.0.0", "1.1.0");
    public int CallCount { get; private set; }
    public string LastModuleId { get; private set; } = string.Empty;
    public Task<WebModuleUpdateInstallOperationResult> InstallAsync(string moduleId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++; LastModuleId = moduleId;
        return Task.FromResult(NextResult);
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
file sealed class ImportOperations : IWebModuleImportOperations
{
    public WebModuleImportOperationResult NextResult { get; set; } =
        new(WebModuleImportDisposition.Cancelled, null);
    public Exception? Failure { get; set; }
    public int CallCount { get; private set; }
    public Task<WebModuleImportOperationResult> ImportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        if (Failure is not null) throw Failure;
        return Task.FromResult(NextResult);
    }
}
file sealed class ManagementOperations : IWebModuleManagementOperations
{
    public WebModuleManagementResult NextResult { get; set; } = WebModuleManagementResult.Succeeded;
    public int OpenCount { get; private set; }
    public int RemoveCount { get; private set; }
    public Task<WebModuleManagementResult> OpenDirectoryAsync(string moduleId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); OpenCount++; return Task.FromResult(NextResult); }
    public Task<WebModuleManagementResult> RemoveAsync(string moduleId, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); RemoveCount++; return Task.FromResult(NextResult); }
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
        return new(SettingsLanguages.Create("en-US", "en-US"), AppearancePresetIds.QingDefault,
            false, "Ask", "Ask before closing.", true, true, false,
            "FloatingBadge", "Task Scheduler", "Healthy", "Startup registration is healthy.");
    }
}
file sealed class FontSnapshotSource : IWebFontSettingsSnapshotSource
{
    private static readonly string ImportedHash = new('b', 64);
    public WebFontSnapshot Read() => new(
        new WebFontSelection("system:Segoe UI", FontPreferenceSources.System, "Segoe UI", "Segoe UI", null),
        [
            new WebFontOption(FontPreferenceIds.Default, FontPreferenceSources.Default, "Default", null, null),
            new WebFontOption("system:Segoe UI", FontPreferenceSources.System, "Segoe UI", "Segoe UI", null),
            new WebFontOption(FontPreferenceIds.Imported(ImportedHash), FontPreferenceSources.Imported,
                "Imported Sans", "Imported Sans", FontSettingsService.ResourceUrlPrefix + ImportedHash + ".ttf")
        ]);
}
file sealed class FontOperations : IWebFontSettingsOperations
{
    public WebFontMutationResult SetResult { get; set; } = WebFontMutationResult.Succeeded;
    public WebFontMutationResult ImportResult { get; set; } = WebFontMutationResult.Cancelled;
    public int SetCount { get; private set; }
    public int ImportCount { get; private set; }
    public int RefreshCount { get; private set; }
    public Task<WebFontMutationResult> SetAsync(string id, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); SetCount++; return Task.FromResult(SetResult); }
    public Task<WebFontMutationResult> ImportAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); ImportCount++; return Task.FromResult(ImportResult); }
    public Task<WebFontMutationResult> RefreshAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); RefreshCount++; return Task.FromResult(WebFontMutationResult.Succeeded); }
}
file sealed class SettingsMutationSource(bool initialValue) : IWebSettingsSnapshotSource, IWebSettingsMutation
{
    public string LanguageCode { get; private set; } = "en-US";
    public string AppearancePresetId { get; private set; } = AppearancePresetIds.QingDefault;
    public bool ShowLogsInSidebar { get; private set; } = initialValue;
    public MainWindowCloseBehavior MainWindowCloseBehavior { get; private set; } = MainWindowCloseBehavior.Ask;
    public StartupPresentationMode StartupPresentationMode { get; private set; } = StartupPresentationMode.FloatingBadge;
    public bool LaunchAtLogin { get; private set; } = true;
    public bool CanConfigureLaunchAtLogin { get; set; } = true;
    public bool CanRepairStartup { get; set; }
    public bool RepairReturnsDisabled { get; set; }
    public bool FailWrites { get; set; }
    public int WriteCount { get; private set; }
    public int CloseWriteCount { get; private set; }
    public int PresentationWriteCount { get; private set; }
    public int LaunchWriteCount { get; private set; }
    public int RepairWriteCount { get; private set; }
    public int LanguageWriteCount { get; private set; }
    public int AppearanceWriteCount { get; private set; }
    public WebSettingsSnapshotValues Read() => new(SettingsLanguages.Create(LanguageCode, LanguageCode == "system" ? "en-US" : LanguageCode), AppearancePresetId, ShowLogsInSidebar,
        MainWindowCloseBehavior.ToString(), "Ask before closing.", LaunchAtLogin, CanConfigureLaunchAtLogin, CanRepairStartup, StartupPresentationMode.ToString(), "Task Scheduler", CanRepairStartup ? "Degraded" : "Healthy", CanRepairStartup ? "Startup registration requires repair." : "Startup registration is healthy.");
    public Task<WebSettingsMutationResult> SetLanguageAsync(string languageCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrites) return Task.FromResult(WebSettingsMutationResult.Failed);
        LanguageWriteCount++;
        LanguageCode = languageCode;
        return Task.FromResult(WebSettingsMutationResult.Succeeded);
    }
    public Task SetShowLogsInSidebarAsync(bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrites) throw new IOException("Synthetic settings write failure.");
        WriteCount++;
        ShowLogsInSidebar = value;
        return Task.CompletedTask;
    }
    public Task<WebSettingsMutationResult> SetAppearancePresetAsync(string presetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWrites) return Task.FromResult(WebSettingsMutationResult.Failed);
        AppearanceWriteCount++;
        AppearancePresetId = presetId;
        return Task.FromResult(WebSettingsMutationResult.Succeeded);
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
    public Task<WebSettingsMutationResult> SetLaunchAtLoginAsync(bool value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanConfigureLaunchAtLogin) return Task.FromResult(WebSettingsMutationResult.Unavailable);
        if (FailWrites) return Task.FromResult(WebSettingsMutationResult.Failed);
        LaunchWriteCount++; LaunchAtLogin = value;
        return Task.FromResult(WebSettingsMutationResult.Succeeded);
    }
    public Task<WebSettingsMutationResult> RepairStartupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanRepairStartup) return Task.FromResult(WebSettingsMutationResult.Unavailable);
        if (FailWrites) return Task.FromResult(WebSettingsMutationResult.Failed);
        RepairWriteCount++; CanRepairStartup = false; LaunchAtLogin = !RepairReturnsDisabled;
        return Task.FromResult(WebSettingsMutationResult.Succeeded);
    }
}

file static class SettingsLanguages
{
    private static readonly WebSettingsLanguageOption[] Options =
    [
        new("system", "System Default", "跟随系统"),
        new("zh-CN", "Simplified Chinese", "简体中文"),
        new("en-US", "English", "English")
    ];

    public static WebSettingsLanguage Create(string code, string effectiveCode)
    {
        var selected = Options.First(option => option.Code == code);
        return new(code, effectiveCode, selected.DisplayName, Options);
    }
}
