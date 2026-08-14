using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using QingToolbox.Abstractions.Localization;
using QingToolbox.Abstractions.Modules;
using QingToolbox.Modules.Launcher;

var root = FindRoot(AppContext.BaseDirectory);
var temp = Path.Combine(Path.GetTempPath(), "qing-launcher-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var exeA = Path.Combine(temp, "Alpha.exe");
    var exeB = Path.Combine(temp, "Beta.exe");
    File.WriteAllText(exeA, "not executed by smoke");
    File.WriteAllText(exeB, "not executed by smoke");

    var store = new LauncherStore(Path.Combine(temp, "store"));
    Require(store.Items.Count == 0, "Fresh store was not empty.");
    Require(LauncherItemResolver.TryResolve(exeA, out var resolvedA) && resolvedA is not null, "Temporary .exe was not resolved.");
    Require(store.Add(resolvedA!, null, out var itemA), "First .exe was not added.");
    Require(!store.Add(resolvedA!, null, out _), "Duplicate target was added.");
    Require(LauncherItemResolver.TryResolve(exeB, out var resolvedB) && resolvedB is not null, "Second .exe was not resolved.");
    Require(store.Add(resolvedB!, null, out var itemB), "Second .exe was not added.");
    var shortcut = Path.Combine(temp, "Beta.lnk");
    if (TryCreateShortcut(shortcut, exeB))
    {
        Require(LauncherItemResolver.TryResolve(shortcut, out var resolvedShortcut) && resolvedShortcut is not null, "Windows .lnk was not resolved.");
        Require(string.Equals(Path.GetFullPath(resolvedShortcut!.Target), Path.GetFullPath(exeB), StringComparison.OrdinalIgnoreCase), "Resolved .lnk target changed.");
        Console.WriteLine("Windows .lnk integration smoke passed.");
    }
    else Console.WriteLine("Windows .lnk integration unavailable; resolver unit path remains covered.");
    Require(store.SetCustomOrder([itemB.Id, itemA.Id]), "Custom order was rejected.");
    Require(store.Items.Select(item => item.Id).SequenceEqual([itemB.Id, itemA.Id]), "Custom order did not persist in memory.");
    Require(store.SetSortMode("alphabetical"), "Alphabetical mode was rejected.");
    Require(store.Items.Select(item => item.Id).SequenceEqual([itemB.Id, itemA.Id]), "Alphabetical mode changed custom storage order.");
    Require(store.MarkLaunched(itemA.Id, DateTimeOffset.UtcNow), "Launch timestamp was not recorded.");
    var reloaded = new LauncherStore(Path.Combine(temp, "store"));
    Require(reloaded.Items.Select(item => item.Id).SequenceEqual([itemB.Id, itemA.Id]), "Store reload lost custom order.");
    Require(reloaded.Snapshot().Recent.Count == 1 && reloaded.Snapshot().Recent[0].Id == itemA.Id, "Recent ordering was not restored.");
    var corruptDirectory = Path.Combine(temp, "corrupt");
    Directory.CreateDirectory(corruptDirectory);
    File.WriteAllText(Path.Combine(corruptDirectory, "launcher.json"), "{not-json");
    Require(new LauncherStore(corruptDirectory).Items.Count == 0, "Corrupt JSON did not safely reset the store.");

    var dataDirectory = Path.Combine(temp, "module-data");
    var fakeStarter = new FakeProcessStarter { Result = true };
    var fakeRegistration = new FakeHotkeyRegistration();
    await using var module = new LauncherModule(fakeStarter, fakeRegistration);
    var actions = new List<ModuleHostWindowAction>();
    module.HostWindowActionRequested += (_, args) => actions.Add(args.Action);
    await module.OnLoadAsync(new ModuleContext
    {
        ModuleId = "qing.launcher",
        ModuleDirectory = Path.Combine(root, "modules", "Launcher"),
        DataDirectory = dataDirectory,
        Localization = new SmokeLocalization(),
    });
    await module.OnActivateAsync();
    Require(fakeRegistration.Registered.Count == 1, "Default hotkey was not registered on activate.");
    await module.HandleExternalFilesDroppedAsync([exeA, exeB]);
    var state = await module.HandleWebRequestAsync("getState", null);
    var stateRoot = state!.Value;
    var ids = stateRoot.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
    Require(ids.Length == 2, "Drop did not add both temporary executables.");
    await module.HandleWebRequestAsync("launchItem", JsonSerializer.SerializeToElement(new { id = ids[0] }));
    Require(fakeStarter.Started.Count == 1, "Launch seam was not invoked.");
    Require(actions.Contains(ModuleHostWindowAction.Hide), "Successful launch did not request Hide.");
    fakeStarter.Result = false;
    var failedBefore = (await module.HandleWebRequestAsync("getState", null))!.Value.GetProperty("recent").GetArrayLength();
    try { await module.HandleWebRequestAsync("launchItem", JsonSerializer.SerializeToElement(new { id = ids[1] })); throw new InvalidOperationException("Failed launch unexpectedly succeeded."); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("could not be started", StringComparison.OrdinalIgnoreCase)) { }
    var failedAfter = (await module.HandleWebRequestAsync("getState", null))!.Value.GetProperty("recent").GetArrayLength();
    Require(failedBefore == failedAfter, "Failed launch changed recent history.");
    module.TriggerHotkeyForTest();
    Require(actions.Contains(ModuleHostWindowAction.Toggle), "Hotkey did not request Toggle.");
    await module.HandleWebRequestAsync("hideWindow", null);
    Require(actions.Count(action => action == ModuleHostWindowAction.Hide) >= 2, "hideWindow did not request Hide.");

    var replacementRegistration = new FakeHotkeyRegistration();
    var replacement = new LauncherHotkeyService(replacementRegistration);
    Require(replacement.Activate(LauncherHotkeySpec.Default), "Hotkey test activation failed.");
    var oldId = replacementRegistration.Registered.Keys.Single();
    replacementRegistration.FailNext = true;
    Require(!replacement.Replace(new LauncherHotkeySpec(KeyLabel: "L", VirtualKey: 0x4C)), "Failed hotkey replacement succeeded.");
    Require(replacementRegistration.Registered.ContainsKey(oldId), "Failed replacement removed the old hotkey.");
    Require(replacement.Replace(new LauncherHotkeySpec(KeyLabel: "L", VirtualKey: 0x4C)), "Valid hotkey replacement failed.");
    replacement.Deactivate();
    Require(replacementRegistration.Registered.Count == 0, "Hotkey deactivation leaked a registration.");

    using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "modules", "Launcher", "module.json"))))
    {
        var value = manifest.RootElement;
        Require(value.GetProperty("id").GetString() == "qing.launcher", "Launcher module id changed.");
        Require(value.GetProperty("version").GetString() == "0.1.0" && value.GetProperty("minimumHostVersion").GetString() == "0.2.6-alpha", "Launcher version contract changed.");
        Require(value.GetProperty("uiKind").GetString() == "Web" && value.GetProperty("runtimeIsolation").GetString() == "OutOfProcess" && value.GetProperty("webEntry").GetString() == "ui/index.html", "Launcher Web manifest contract changed.");
        Require(value.GetProperty("loadMode").GetString() == "Manual", "Launcher must remain manually loaded.");
    }
    foreach (var culture in new[] { "en-US", "zh-CN" })
        Require(File.Exists(Path.Combine(root, "modules", "Launcher", "i18n", culture + ".json")), $"Missing {culture} resources.");
    Require(File.Exists(Path.Combine(root, "modules", "Launcher", "ui", "index.html")), "Launcher UI build output is missing.");

    if (args.Length >= 2 && args[0] == "--package") VerifyPackage(args[1]);
    Console.WriteLine("Qing Launcher smoke test passed.");
}
finally { try { Directory.Delete(temp, true); } catch { } }

static void VerifyPackage(string packagePath)
{
    using var archive = ZipFile.OpenRead(packagePath);
    var entries = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
    foreach (var required in new[] { "module.json", "QingToolbox.Modules.Launcher.dll", "icon.svg", "i18n/en-US.json", "i18n/zh-CN.json", "ui/index.html" })
        Require(entries.Contains(required), $"Package is missing {required}.");
    Require(entries.Any(entry => entry.StartsWith("ui/assets/", StringComparison.Ordinal)), "Package has no UI assets.");
    Require(!entries.Any(entry => entry.Contains("web/src", StringComparison.OrdinalIgnoreCase) || entry.Contains("node_modules", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || entry.EndsWith("package.json", StringComparison.OrdinalIgnoreCase) || entry.EndsWith("package-lock.json", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".map", StringComparison.OrdinalIgnoreCase)), "Package contains forbidden development content.");
}

static string FindRoot(string path)
{
    var current = new DirectoryInfo(path);
    while (current is not null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, "modules")) && Directory.Exists(Path.Combine(current.FullName, "scripts"))) return current.FullName;
        current = current.Parent;
    }
    throw new DirectoryNotFoundException("Repository root not found.");
}

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

static bool TryCreateShortcut(string path, string target)
{
    object? shell = null;
    object? shortcut = null;
    try
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return false;
        shell = Activator.CreateInstance(shellType);
        shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
        if (shortcut is null) return false;
        var shortcutType = shortcut.GetType();
        shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [target]);
        shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, ["--smoke"]);
        shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [Path.GetDirectoryName(target)!]);
        shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        return File.Exists(path);
    }
    catch (COMException) { return false; }
    catch (InvalidOperationException) { return false; }
    finally
    {
        if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
        if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
    }
}

sealed class FakeProcessStarter : ILauncherProcessStarter
{
    public bool Result { get; set; }
    public List<LauncherItem> Started { get; } = [];
    public bool Start(LauncherItem item) { if (Result) Started.Add(item); return Result; }
}

sealed class FakeHotkeyRegistration : ILauncherHotkeyRegistration
{
    public Dictionary<int, LauncherHotkeySpec> Registered { get; } = [];
    public bool FailNext { get; set; }
    public bool Register(int id, LauncherHotkeySpec hotkey) { if (FailNext) { FailNext = false; return false; } Registered[id] = hotkey; return true; }
    public bool Unregister(int id) => Registered.Remove(id);
}

sealed class SmokeLocalization : ILocalizationService
{
    public CultureInfo CurrentCulture => CultureInfo.InvariantCulture;
    public string CurrentLanguageCode => "en-US";
    public event EventHandler? CultureChanged { add { } remove { } }
    public string GetString(string key) => key;
    public string GetString(string key, params object[] args) => string.Format(CurrentCulture, key, args);
    public string GetModuleString(string moduleId, string key, string? fallback = null) => fallback ?? key;
    public string GetModuleString(string moduleId, string key, string? fallback, params object[] args) => string.Format(CurrentCulture, fallback ?? key, args);
}
