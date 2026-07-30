using System.IO;
using System.Text.Json;
using QingToolbox.Modules.TextTools;
using QingToolbox.Modules.WindowTopmost;

var root = FindRoot(AppContext.BaseDirectory);
Require(TextOperations.Base64Encode("QingToolbox") == "UWluZ1Rvb2xib3g=", "Base64 encoding changed.");
Require(TextOperations.Base64Decode("UWluZ1Rvb2xib3g=") == "QingToolbox", "Base64 decoding changed.");
Require(TextOperations.FormatJson("{\"value\":1}").Contains(Environment.NewLine), "JSON formatting changed.");
Require(TextOperations.MinifyJson("{ \"value\" : 1 }") == "{\"value\":1}", "JSON minification changed.");
Require(TextOperations.RemoveEmptyLines("one\n\n two ").Contains("two"), "Line cleanup changed.");
Require(TextOperations.Base64Encode(string.Empty) == string.Empty, "Empty text must remain safe.");
try { _ = TextOperations.Base64Decode("not base64"); throw new InvalidOperationException("Invalid Base64 was accepted."); }
catch (FormatException) { }
Require(!WindowTopmostService.SetTopmost(0, true), "An invalid window handle was accepted.");

foreach (var name in new[] { "TextTools", "PowerGuard", "WindowTopmost" })
{
    var moduleRoot = Path.Combine(root, "modules", name);
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(moduleRoot, "module.json")));
    var element = manifest.RootElement;
    Require(element.GetProperty("loadMode").GetString() == "Manual", $"{name} must remain manually loaded.");
    Require(element.GetProperty("minimumHostVersion").GetString() == "0.1.0", $"{name} minimum host version changed unexpectedly.");
    Require(File.Exists(Path.Combine(moduleRoot, "icon.svg")), $"{name} icon is missing.");
    foreach (var culture in new[] { "en-US", "zh-CN" })
    {
        using var resource = JsonDocument.Parse(File.ReadAllText(Path.Combine(moduleRoot, "i18n", culture + ".json")));
        Require(resource.RootElement.TryGetProperty("module.name", out _) && resource.RootElement.TryGetProperty("module.description", out _),
            $"{name} {culture} module metadata is incomplete.");
    }
}
Console.WriteLine("Starter module smoke test passed.");

static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
static string FindRoot(string path){var current=new DirectoryInfo(path);while(current is not null){if(Directory.Exists(Path.Combine(current.FullName,"modules"))&&Directory.Exists(Path.Combine(current.FullName,"scripts")))return current.FullName;current=current.Parent;}throw new DirectoryNotFoundException("Repository root not found.");}
