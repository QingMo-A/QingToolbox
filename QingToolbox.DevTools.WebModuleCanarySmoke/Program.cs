using System.Text.Json;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;
using QingToolbox.DevTools.WebModuleCanary;
using QingToolbox.ModuleLoader;

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
    await module.OnActivateAsync();
    var state = await module.HandleWebRequestAsync("getState", null, CancellationToken.None);
    Require(state is { } stateValue && stateValue.GetProperty("loaded").GetBoolean() && stateValue.GetProperty("active").GetBoolean(),
        "Web canary getState must observe loaded/active lifecycle.");
    var echoed = await module.HandleWebRequestAsync("echo", JsonSerializer.SerializeToElement(new { value = "ok" }), CancellationToken.None);
    Require(echoed is { } echoValue && echoValue.GetProperty("value").GetString() == "ok", "Web canary echo must return payload.");
    Require(events.Any(value => value.Name == "stateChanged"), "Web canary must emit stateChanged.");
    await module.OnDeactivateAsync();
    await handle.DisposeAsync();
    var lifecycle = File.ReadAllLines(Path.Combine(dataRoot, discovered.Manifest.Id, "lifecycle.log"));
    Console.WriteLine("Lifecycle: " + string.Join(",", lifecycle));
    Require(lifecycle.SequenceEqual(["load", "activate", "deactivate", "unload", "dispose"]), "Web lifecycle must clean up in order.");
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
