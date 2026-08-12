using System.IO;
using System.Net;
using System.Text.Json;
using QingToolbox.Modules.QingTransfer;

var fields = new Dictionary<string, string?>
{
    ["v"] = "1", ["pf"] = "windows", ["name"] = "Desk", ["cap"] = "file"
};
var peer = QingTransferMetadata.Parse("Desk._qingtransfer._tcp.local.", fields, "desk.local", 43125,
    new[] { IPAddress.Loopback }, DateTimeOffset.UtcNow);
Require(peer is not null && peer.ServiceName == "Desk._qingtransfer._tcp.local" && peer.Port == 43125, "Valid metadata was rejected.");
var unsupported = new Dictionary<string, string?>(fields) { ["v"] = "2" };
Require(QingTransferMetadata.Parse("Desk._qingtransfer._tcp.local", unsupported, null, 1) is null, "Unknown protocol version was accepted.");
var noFile = new Dictionary<string, string?>(fields) { ["cap"] = "chat" };
Require(QingTransferMetadata.Parse("Desk._qingtransfer._tcp.local", noFile, null, 1) is null, "Missing file capability was accepted.");
var control = new Dictionary<string, string?>(fields) { ["name"] = "bad\u0001name" };
Require(QingTransferMetadata.Parse("Desk._qingtransfer._tcp.local", control, null, 1) is null, "Control characters were accepted.");
Require(QingTransferMetadata.Parse("Desk._qingtransfer._tcp.local", fields, null, 65536) is null, "Invalid port was accepted.");
var table = new QingTransferPeerTable();
Require(table.Upsert(peer!), "First peer insert was not reported.");
Require(!table.Upsert(peer!), "Duplicate peer insert was reported as changed.");
Require(table.Remove(peer!.ServiceName) && table.Snapshot().Count == 0, "Peer removal failed.");
var created = QingTransferMetadata.Create("android", "Phone");
Require(created["v"] == "1" && created["pf"] == "android" && created["cap"] == "file", "TXT metadata changed.");

var root = FindRoot(AppContext.BaseDirectory);
var moduleRoot = Path.Combine(root, "modules", "QingTransfer");
using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(moduleRoot, "module.json"))))
{
    Require(manifest.RootElement.GetProperty("id").GetString() == "qing.qingtransfer", "Module id changed.");
    Require(manifest.RootElement.GetProperty("loadMode").GetString() == "Manual", "Module must remain manually loaded.");
}
foreach (var culture in new[] { "en-US", "zh-CN" })
{
    using var resource = JsonDocument.Parse(File.ReadAllText(Path.Combine(moduleRoot, "i18n", culture + ".json")));
    Require(resource.RootElement.TryGetProperty("view.empty", out _) && resource.RootElement.TryGetProperty("actions.refresh", out _), $"{culture} resources are incomplete.");
}

// Exercise idempotent cleanup. DNS-SD can be unavailable on a restricted Windows
// image; in that case the service reports the platform error and still cleans up.
await using (var service = new QingTransferDiscoveryService("QingTransfer Smoke"))
{
    try
    {
        await service.StartAsync();
        await service.StartAsync();
        await service.StopAsync();
        await service.StopAsync();
    }
    catch (PlatformNotSupportedException) { Console.WriteLine("DNS-SD unavailable; cleanup path verified."); }
}
await QingTransferProtocolTest.RunAsync();
Console.WriteLine("QingTransfer smoke test passed.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
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
