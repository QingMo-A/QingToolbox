using System.Text.Json;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;
using QingToolbox.DevTools.WebModuleCanary;
using QingToolbox.ModuleLoader;
using QingToolbox.ModuleHost;
using System.Threading;
using System.Windows;

Require(!typeof(CanaryModule).IsAssignableTo(typeof(IModuleHostWindowPresentationSource)),
    "The default Web canary must not opt into overlay presentation.");
Require(!WebModuleWindowPresentation.IsOverlay(ModuleHostWindowPresentationMode.Standard),
    "Standard presentation must remain the default.");
Require(WebModuleWindowPresentation.CanResumeFromSuspension(ModuleHostWindowPresentationMode.Overlay, ModuleHostWindowAction.Show) &&
        WebModuleWindowPresentation.CanResumeFromSuspension(ModuleHostWindowPresentationMode.Overlay, ModuleHostWindowAction.Toggle),
    "Overlay Show/Toggle actions must resume from Shell window suspension.");
Require(!WebModuleWindowPresentation.CanResumeFromSuspension(ModuleHostWindowPresentationMode.Standard, ModuleHostWindowAction.Show) &&
        !WebModuleWindowPresentation.CanResumeFromSuspension(ModuleHostWindowPresentationMode.Overlay, ModuleHostWindowAction.Hide),
    "Standard windows and non-opening actions must preserve suspension.");
Require(new OverlayPresentationProbe().HostWindowPresentationMode == ModuleHostWindowPresentationMode.Overlay,
    "An opt-in presentation source must expose Overlay mode.");
Require(OverlayDismissPolicy.OnDeactivated(ModuleHostWindowPresentationMode.Overlay, false, true, false, false) == OverlayDismissDecision.Hide,
    "Inactive Overlay without a pressed mouse button must hide immediately.");
Require(OverlayDismissPolicy.OnDeactivated(ModuleHostWindowPresentationMode.Overlay, false, true, false, true) == OverlayDismissDecision.Defer,
    "Inactive Overlay with a pressed mouse button must defer dismissal.");
Require(OverlayDismissPolicy.OnDeferredTick(false, true, false, false, false, long.MaxValue) == OverlayDismissDecision.Hide,
    "Deferred Overlay release outside the window must hide.");
Require(OverlayDismissPolicy.OnDeferredTick(false, true, true, false, false, long.MaxValue) == OverlayDismissDecision.Keep,
    "Reactivated Overlay must remain visible.");
Require(OverlayDismissPolicy.OnDeferredTick(false, true, false, true, false, long.MaxValue) == OverlayDismissDecision.Defer,
    "Active external file drag must keep deferred dismissal monitoring alive.");
Require(OverlayDismissPolicy.OnDeferredTick(false, true, false, false, false, 100) == OverlayDismissDecision.Keep,
    "A recent OS FileDrop must keep Overlay visible through mouse release ordering.");
Require(OverlayDismissPolicy.OnDeferredTick(false, true, false, false, false, OverlayDismissPolicy.DropGraceMilliseconds) == OverlayDismissDecision.Hide,
    "A drag that left without dropping must hide after release.");
Require(OverlayDismissPolicy.OnDeactivated(ModuleHostWindowPresentationMode.Standard, false, true, false, false) == OverlayDismissDecision.Keep,
    "Standard Web module windows must not enable click-away dismissal.");
Require(WebModuleWindow.ShouldSuppressSystemMenu(ModuleHostWindowPresentationMode.Overlay, 0x0112, 0xF100),
    "Overlay windows must suppress the Alt system menu so Web content can record Alt+Space.");
Require(!WebModuleWindow.ShouldSuppressSystemMenu(ModuleHostWindowPresentationMode.Standard, 0x0112, 0xF100),
    "Standard Web module windows must retain their native system menu.");
Require(!WebModuleWindow.ShouldSuppressSystemMenu(ModuleHostWindowPresentationMode.Overlay, 0x0100, 0xF100),
    "Overlay system-menu suppression must not consume ordinary keyboard messages.");
Require(WebModuleWindow.ShouldForwardAltSpace(ModuleHostWindowPresentationMode.Overlay, 0x20, true),
    "Overlay WebView accelerators must forward Alt+Space to Web content.");
Require(!WebModuleWindow.ShouldForwardAltSpace(ModuleHostWindowPresentationMode.Standard, 0x20, true) &&
        !WebModuleWindow.ShouldForwardAltSpace(ModuleHostWindowPresentationMode.Overlay, 0x20, false) &&
        !WebModuleWindow.ShouldForwardAltSpace(ModuleHostWindowPresentationMode.Overlay, 0x41, true),
    "Alt+Space forwarding must remain isolated from Standard windows and unrelated keys.");

var dragCancelSequence = new[]
{
    OverlayDismissPolicy.OnDeactivated(ModuleHostWindowPresentationMode.Overlay, false, true, false, true),
    OverlayDismissPolicy.OnDeferredTick(false, true, false, true, true, long.MaxValue),
    OverlayDismissPolicy.OnDeferredTick(false, true, false, false, true, long.MaxValue),
    OverlayDismissPolicy.OnDeferredTick(false, true, false, false, false, long.MaxValue),
};
Require(dragCancelSequence.SequenceEqual(new[]
    {
        OverlayDismissDecision.Defer,
        OverlayDismissDecision.Defer,
        OverlayDismissDecision.Defer,
        OverlayDismissDecision.Hide,
    }),
    "Explorer drag enter, leave and outside release must keep monitoring until Overlay hides.");

var successfulDropSequence = new[]
{
    OverlayDismissPolicy.OnDeactivated(ModuleHostWindowPresentationMode.Overlay, false, true, false, true),
    OverlayDismissPolicy.OnDeferredTick(false, true, false, true, true, long.MaxValue),
    OverlayDismissPolicy.OnDeferredTick(false, true, false, false, false, 100),
};
Require(successfulDropSequence.SequenceEqual(new[]
    {
        OverlayDismissDecision.Defer,
        OverlayDismissDecision.Defer,
        OverlayDismissDecision.Keep,
    }),
    "A successful Explorer drop must end deferred monitoring while preserving Overlay visibility.");
var presentationError = default(Exception);
var presentationThread = new Thread(() =>
{
    try
    {
        var standard = new Window();
        WebModuleWindowPresentation.Apply(standard, ModuleHostWindowPresentationMode.Standard);
        Require(standard.WindowStyle == WindowStyle.SingleBorderWindow && standard.ResizeMode == ResizeMode.CanResize &&
                standard.ShowInTaskbar && !standard.AllowsTransparency && standard.Width == 900 && standard.Height == 680,
            "Standard WebModuleWindow presentation must retain its normal window shape.");
        standard.Topmost = true;
        WebModuleWindowPresentation.EnsureTopmost(standard, ModuleHostWindowPresentationMode.Standard);
        Require(standard.Topmost, "Standard presentation must not rewrite an existing Topmost value.");

        var overlay = new Window();
        WebModuleWindowPresentation.Apply(overlay, ModuleHostWindowPresentationMode.Overlay);
        Require(overlay.WindowStyle == WindowStyle.None && overlay.ResizeMode == ResizeMode.NoResize &&
                !overlay.ShowInTaskbar && overlay.AllowsTransparency && overlay.Background == System.Windows.Media.Brushes.Transparent &&
                overlay.WindowStartupLocation == WindowStartupLocation.CenterScreen && overlay.Width == 1000 && overlay.Height == 680 && overlay.Topmost,
            "Overlay WebModuleWindow presentation must be frameless, transparent, centered and topmost.");
        overlay.Topmost = false;
        WebModuleWindowPresentation.EnsureTopmost(overlay, ModuleHostWindowPresentationMode.Overlay);
        Require(overlay.Topmost, "Overlay presentation must reassert Topmost after hide/show or toggle.");
    }
    catch (Exception exception) { presentationError = exception; }
});
presentationThread.SetApartmentState(ApartmentState.STA);
presentationThread.Start();
presentationThread.Join();
if (presentationError is not null) throw presentationError;

var canaryOutput = Path.GetDirectoryName(typeof(CanaryModule).Assembly.Location)!;
var canaryDirectory = Path.Combine(Path.GetTempPath(), "QingToolbox-WebCanary-Module-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(canaryDirectory);
File.Copy(Path.Combine(canaryOutput, "QingToolbox.DevTools.WebModuleCanary.dll"), Path.Combine(canaryDirectory, "QingToolbox.DevTools.WebModuleCanary.dll"));
File.Copy(Path.Combine(canaryOutput, "module.json"), Path.Combine(canaryDirectory, "module.json"));
CopyDirectory(Path.Combine(canaryOutput, "ui"), Path.Combine(canaryDirectory, "ui"));
var manifestPath = Path.Combine(canaryDirectory, "module.json");
var manifest = await new ModuleManifestReader().ReadAsync(manifestPath) ?? throw new InvalidDataException("Canary manifest missing.");
var errors = new ModuleManifestValidator().Validate(manifest, canaryDirectory, manifestPath);
var discovered = new DiscoveredModule
{
    Manifest = manifest,
    ModuleDirectory = canaryDirectory,
    ManifestPath = manifestPath,
    Errors = errors
};
Require(discovered.IsValid, string.Join("; ", errors.Select(error => error.Code)));
Require(discovered.Manifest.Entry == "QingToolbox.DevTools.WebModuleCanary.dll" &&
        discovered.Manifest.WebEntry == "ui/index.html", "Web canary manifest must require both backend and frontend entries.");
var profileRoot = Path.Combine(Path.GetTempPath(), "QingToolbox-WebCanary-" + Guid.NewGuid().ToString("N"));
var dataRoot = Path.Combine(profileRoot, "data");
Directory.CreateDirectory(dataRoot);
try
{
    await using var handle = await new InProcessModuleLoader(new SmokeLocalization()).LoadAsync(discovered, dataRoot);
    Require(handle.Module is IWebToolModule, "OutOfProcess + Web must load the IWebToolModule contract.");
    var module = (IWebToolModule)handle.Module;
    var events = new List<ModuleWebEventArgs>();
    module.WebEvent += (_, value) => events.Add(value);
    Require(module is IModuleHostWindowActionSource, "Web canary must expose the optional host window action contract.");
    var actions = new List<ModuleHostWindowAction>();
    var actionSource = (IModuleHostWindowActionSource)module;
    actionSource.HostWindowActionRequested += (_, value) => actions.Add(value.Action);
    await module.OnActivateAsync();
    var state = await module.HandleWebRequestAsync("getState", null, CancellationToken.None);
    Require(state is { } stateValue && stateValue.GetProperty("loaded").GetBoolean() && stateValue.GetProperty("active").GetBoolean(),
        "Web canary getState must observe loaded/active lifecycle.");
    var echoed = await module.HandleWebRequestAsync("echo", JsonSerializer.SerializeToElement(new { value = "ok" }), CancellationToken.None);
    Require(echoed is { } echoValue && echoValue.GetProperty("value").GetString() == "ok", "Web canary echo must return payload.");
    Require(events.Any(value => value.Name == "stateChanged"), "Web canary must emit stateChanged.");
    var background = await module.HandleWebRequestAsync("emitBackgroundEvent", null, CancellationToken.None);
    Require(background is { } backgroundValue && backgroundValue.GetProperty("emitted").GetBoolean() &&
            events.Any(value => value.Name == "backgroundEvent"), "Web canary must emit a background event.");
    foreach (var method in new[] { "requestHide", "requestShow", "requestToggle" })
    {
        var action = await module.HandleWebRequestAsync(method, null, CancellationToken.None);
        Require(action is { } actionValue && actionValue.GetProperty("requested").GetBoolean(),
            $"Web canary {method} must acknowledge its host action request.");
    }
    Require(actions.SequenceEqual([ModuleHostWindowAction.Hide, ModuleHostWindowAction.Show, ModuleHostWindowAction.Toggle]),
        "Web canary host actions must preserve request order.");
    var dropDirectory = Path.Combine(Path.GetTempPath(), "QingToolbox-WebCanary-Drop-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dropDirectory);
    var droppedFile = Path.Combine(dropDirectory, "app.exe");
    await File.WriteAllTextAsync(droppedFile, "canary");
    await ((IWebExternalFileDropSink)module).HandleExternalFilesDroppedAsync([droppedFile]);
    var dropEvent = events.LastOrDefault(value => value.Name == "externalDrop");
    Require(dropEvent?.Payload is { } dropPayload && dropPayload.GetProperty("count").GetInt32() == 1 &&
            dropPayload.GetProperty("names")[0].GetString() == "app.exe",
        "Web canary external drop must expose only the basename to WebEvent.");
    Directory.Delete(dropDirectory, true);
    await module.OnDeactivateAsync();
    await handle.DisposeAsync();
    var lifecycle = File.ReadAllLines(Path.Combine(dataRoot, discovered.Manifest.Id, "lifecycle.log"));
    Console.WriteLine("Lifecycle: " + string.Join(",", lifecycle));
    Require(lifecycle.SequenceEqual(["load", "activate", "drop:app.exe", "deactivate", "unload", "dispose"]), "Web lifecycle must clean up in order.");
    Console.WriteLine("Web module backend/bridge lifecycle canary passed.");
}
finally
{
    if (Directory.Exists(profileRoot)) Directory.Delete(profileRoot, true);
    try { if (Directory.Exists(canaryDirectory)) Directory.Delete(canaryDirectory, true); }
    catch (UnauthorizedAccessException) { /* collectible ALC may release after process exit */ }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var path in Directory.EnumerateFiles(source)) File.Copy(path, Path.Combine(destination, Path.GetFileName(path)));
}

sealed class OverlayPresentationProbe : IModuleHostWindowPresentationSource
{
    public ModuleHostWindowPresentationMode HostWindowPresentationMode => ModuleHostWindowPresentationMode.Overlay;
}

sealed class SmokeLocalization : ILocalizationService
{
    public System.Globalization.CultureInfo CurrentCulture => System.Globalization.CultureInfo.InvariantCulture;
    public string CurrentLanguageCode => "en-US";
    public event EventHandler? CultureChanged { add { } remove { } }
    public string GetString(string key) => key;
    public string GetString(string key, params object[] args) => string.Format(key, args);
    public string GetModuleString(string moduleId, string key, string? fallback = null) => fallback ?? key;
    public string GetModuleString(string moduleId, string key, string? fallback, params object[] args) => string.Format(fallback ?? key, args);
}
