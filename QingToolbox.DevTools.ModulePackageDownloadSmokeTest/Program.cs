using System.Net;
using System.Security.Cryptography;
using QingToolbox.Core.Updates;

await Smoke.RunAsync();

static class Smoke
{
    public static async Task RunAsync()
    {
        TestUrls(); await TestVerifiedDownloadAsync(); await TestMetadataBoundariesAsync();
        await TestSizeAndHashFailuresAsync(); await TestAlreadyVerifiedAndConcurrencyAsync(); await TestParallelLimitAsync(); await TestCancellationAsync(); await TestRedirectsAsync();
        Console.WriteLine("Module package download smoke test passed: fresh metadata, trusted redirects, streaming limits, SHA256 and atomic verified storage.");
    }

    private static void TestUrls()
    {
        var valid = Package([1, 2, 3]); OfficialModulePackageTransport.ValidatePackage(valid);
        foreach (var url in new[] {
            "http://github.com/QingMo-A/QingToolbox/releases/download/v1/Demo.qmod",
            "https://github.com/Other/QingToolbox/releases/download/v1/Demo.qmod",
            "https://github.com/QingMo-A/QingToolbox/releases/latest/download/Demo.qmod",
            "https://github.com/QingMo-A/QingToolbox/releases/download/v1/Demo.qmod?q=1",
            "https://user@github.com/QingMo-A/QingToolbox/releases/download/v1/Demo.qmod",
            "https://github.com:444/QingMo-A/QingToolbox/releases/download/v1/Demo.qmod",
            "https://github.com/QingMo-A/QingToolbox/releases/download/v1/Other.qmod" })
            Reject(() => OfficialModulePackageTransport.ValidatePackage(valid with { Url = new(url) }), "unsafe URL");
        Reject(() => OfficialModulePackageTransport.ValidatePackage(valid with { FileName = "../Demo.qmod" }), "unsafe filename");
        Reject(() => OfficialModulePackageTransport.ValidatePackage(valid with { Size = ModulePackageDownloadCoordinator.MaximumModulePackageSize + 1 }), "oversize");
    }

    private static async Task TestVerifiedDownloadAsync()
    {
        var data = Enumerable.Range(0, 200000).Select(x => (byte)x).ToArray();
        await InTemp(async root =>
        {
            var package = Package(data); var checker = new FakeChecker(Result(package)); var transport = new FakeTransport(data);
            await using var coordinator = new ModulePackageDownloadCoordinator(checker, transport, root, new FakeClock(), false);
            var progress = new List<ModulePackageDownloadProgress>();
            var result = await coordinator.DownloadAsync(Request(package), new Progress<ModulePackageDownloadProgress>(x => progress.Add(x)));
            Assert(result.Status == ModulePackageDownloadStatus.Verified && result.VerifiedPackage is not null, "verified result");
            var verified = result.VerifiedPackage!;
            Assert(File.Exists(verified.FilePath) && new FileInfo(verified.FilePath).Length == data.Length, "verified final file");
            Assert(!Directory.EnumerateFiles(root, "*.partial-*", SearchOption.AllDirectories).Any(), "no partial after success");
            Assert(File.Exists(Path.Combine(Path.GetDirectoryName(verified.FilePath)!, "package-record.json")), "record written");
            Assert(transport.Calls == 1 && checker.Calls == 1, "one metadata and package request");
        });
    }

    private static async Task TestMetadataBoundariesAsync()
    {
        var data = new byte[] { 4, 5, 6 }; var package = Package(data);
        foreach (var result in new[] {
            Result(package) with { IsFromStaleCache = true },
            Result(package with { Size = package.Size + 1 }),
            Result(package with { Sha256 = new string('b', 64) }),
            new ModuleUpdateResult("qing.demo", ModuleUpdateStatus.UpToDate)
        })
        await InTemp(async root =>
        {
            var transport = new FakeTransport(data); await using var coordinator = new ModulePackageDownloadCoordinator(new FakeChecker(result), transport, root, new FakeClock(), false);
            var outcome = await coordinator.DownloadAsync(Request(package));
            Assert(outcome.Status is ModulePackageDownloadStatus.MetadataStale or ModulePackageDownloadStatus.MetadataChanged, "metadata rejected");
            Assert(transport.Calls == 0, "metadata rejection before package request");
        });
        await InTemp(async root =>
        {
            var checker = new FakeChecker(Result(package)); var transport = new FakeTransport(data);
            await using var coordinator = new ModulePackageDownloadCoordinator(checker, transport, root, new FakeClock(), true);
            Assert((await coordinator.DownloadAsync(Request(package))).Status == ModulePackageDownloadStatus.DisabledByEnvironment, "ModuleTest disabled");
            Assert(checker.Calls == 0 && transport.Calls == 0 && !Directory.Exists(Path.Combine(root, "ModuleUpdates")), "ModuleTest no IO");
        });
    }

    private static async Task TestSizeAndHashFailuresAsync()
    {
        var expected = new byte[] { 1, 2, 3, 4 }; var package = Package(expected);
        foreach (var (bytes, status) in new[] { (new byte[] { 1, 2 }, ModulePackageDownloadStatus.SizeMismatch),
            (new byte[] { 1, 2, 3, 5 }, ModulePackageDownloadStatus.HashMismatch),
            (new byte[] { 1, 2, 3, 4, 5 }, ModulePackageDownloadStatus.SizeMismatch) })
        await InTemp(async root =>
        {
            await using var coordinator = new ModulePackageDownloadCoordinator(new FakeChecker(Result(package)), new FakeTransport(bytes, null), root, new FakeClock(), false);
            var result = await coordinator.DownloadAsync(Request(package)); Assert(result.Status == status, "size/hash failure");
            Assert(!Directory.EnumerateFiles(root, "*.qmod", SearchOption.AllDirectories).Any(), "failure has no qmod");
            Assert(!Directory.EnumerateFiles(root, "*.partial-*", SearchOption.AllDirectories).Any(), "failure cleans partial");
        });
        await InTemp(async root =>
        {
            await using var coordinator = new ModulePackageDownloadCoordinator(new FakeChecker(Result(package)), new FakeTransport(expected, expected.Length, ["gzip"]), root, new FakeClock(), false);
            Assert((await coordinator.DownloadAsync(Request(package))).Status == ModulePackageDownloadStatus.SizeMismatch, "gzip rejected");
        });
    }

    private static async Task TestAlreadyVerifiedAndConcurrencyAsync()
    {
        var data = new byte[] { 7, 8, 9 }; var package = Package(data);
        await InTemp(async root =>
        {
            var transport = new FakeTransport(data, delay: TimeSpan.FromMilliseconds(50)); var checker = new FakeChecker(Result(package));
            await using var coordinator = new ModulePackageDownloadCoordinator(checker, transport, root, new FakeClock(), false);
            var first = coordinator.DownloadAsync(Request(package)); var second = coordinator.DownloadAsync(Request(package));
            var results = await Task.WhenAll(first, second);
            Assert(results.All(x => x.Status == ModulePackageDownloadStatus.Verified) && transport.Calls == 1, "same package coalesced");
            var third = await coordinator.DownloadAsync(Request(package));
            Assert(third.Status == ModulePackageDownloadStatus.AlreadyVerified && transport.Calls == 1, "existing file reverified without package network");
        });
    }

    private static async Task TestRedirectsAsync()
    {
        var data = new byte[] { 1, 2, 3 }; var package = Package(data); var handler = new RedirectHandler(data, trusted: true);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await using (var response = await new OfficialModulePackageTransport(client).OpenReadAsync(package, default))
            Assert((await ReadAll(response.Content)).SequenceEqual(data), "trusted redirect stream");
        Assert(handler.Urls.Count == 2 && handler.Urls[1].Host == "release-assets.githubusercontent.com", "trusted asset host visited");
        var rejected = new RedirectHandler(data, trusted: false); using var badClient = new HttpClient(rejected);
        try { await new OfficialModulePackageTransport(badClient).OpenReadAsync(package, default); throw new Exception("untrusted redirect accepted"); }
        catch (ModulePackageTransportException ex) { Assert(ex.Status == ModulePackageDownloadStatus.UntrustedRedirect && rejected.Urls.Count == 1, "untrusted redirect not requested"); }
    }
    private static async Task TestCancellationAsync()
    {
        var data = new byte[] { 9, 8, 7, 6 }; var package = Package(data);
        await InTemp(async root =>
        {
            var transport = new CancellableTransport(data); await using var coordinator = new ModulePackageDownloadCoordinator(
                new FakeChecker(Result(package)), transport, root, new FakeClock(), false);
            var task = coordinator.DownloadAsync(Request(package)); await transport.Entered.Task;
            coordinator.Cancel(Request(package));
            Assert((await task).Status == ModulePackageDownloadStatus.Cancelled, "cancelled transfer");
            Assert(!Directory.EnumerateFiles(root, "*.partial-*", SearchOption.AllDirectories).Any() &&
                !Directory.EnumerateFiles(root, "*.qmod", SearchOption.AllDirectories).Any(), "cancel cleans transfer files");
            Assert((await coordinator.DownloadAsync(Request(package))).Status == ModulePackageDownloadStatus.Verified, "retry after cancellation");
        });
    }
    private static async Task TestParallelLimitAsync()
    {
        await InTemp(async root =>
        {
            var requests = Enumerable.Range(1, 3).Select(index =>
            {
                var bytes = new[] { (byte)index };
                var package = Package(bytes) with { FileName = $"Demo{index}.qmod",
                    Url = new($"https://github.com/QingMo-A/QingToolbox/releases/download/v0.2.0/Demo{index}.qmod") };
                return new ModulePackageDownloadRequest($"qing.demo{index}", "0.1.0", SemanticVersion.Parse("0.2.0"), package);
            }).ToArray();
            var transport = new TrackingTransport(); var checker = new MappedChecker(requests);
            await using var coordinator = new ModulePackageDownloadCoordinator(checker, transport, root, new FakeClock(), false);
            var tasks = requests.Select(request => coordinator.DownloadAsync(request)).ToArray();
            await transport.TwoEntered.Task; Assert(transport.MaxActive == 2, "global parallel limit is two");
            transport.Release.SetResult();
            Assert((await Task.WhenAll(tasks)).All(result => result.Status == ModulePackageDownloadStatus.Verified) && transport.MaxActive == 2, "three packages respect parallel limit");
        });
    }

    private static ModuleUpdatePackage Package(byte[] data) => new("Demo.qmod",
        new("https://github.com/QingMo-A/QingToolbox/releases/download/v0.2.0/Demo.qmod"), data.LongLength,
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant());
    private static ModuleUpdateRelease Release(ModuleUpdatePackage package) => new(SemanticVersion.Parse("0.2.0"), ModuleUpdateChannel.Stable,
        ModuleUpdateIdentity.ModuleApiVersion, SemanticVersion.Parse("0.1.0-alpha"), null, DateTimeOffset.UnixEpoch, package, new Dictionary<string, string>());
    private static ModuleUpdateResult Result(ModuleUpdatePackage package) => new("qing.demo", ModuleUpdateStatus.UpdateAvailable, Release(package));
    private static ModulePackageDownloadRequest Request(ModuleUpdatePackage package) => new("qing.demo", "0.1.0", SemanticVersion.Parse("0.2.0"), package);
    private static async Task<byte[]> ReadAll(Stream stream) { using var memory = new MemoryStream(); await stream.CopyToAsync(memory); return memory.ToArray(); }
    private static void Reject(Action action, string name) { try { action(); throw new Exception("Expected rejection: " + name); } catch (ModulePackageTransportException) { } }
    private static void Assert(bool value, string name) { if (!value) throw new Exception("Assertion failed: " + name); }
    private static async Task InTemp(Func<string, Task> action) { var root = Path.Combine(Path.GetTempPath(), "QingToolbox-download-smoke-" + Guid.NewGuid().ToString("N")); try { Directory.CreateDirectory(root); await action(root); } finally { try { Directory.Delete(root, true); } catch { } } }
}

sealed class FakeChecker(ModuleUpdateResult result) : IModuleUpdateChecker
{
    public int Calls { get; private set; }
    public Task<ModuleUpdateResult> CheckModuleAsync(InstalledModuleVersion module, bool manual, CancellationToken token) { Calls++; return Task.FromResult(result); }
    public Task<IReadOnlyDictionary<string, ModuleUpdateResult>> CheckAllInstalledModulesAsync(IReadOnlyCollection<InstalledModuleVersion> modules, bool manual, CancellationToken token) => throw new NotSupportedException();
}
sealed class FakeTransport(byte[] bytes, long? contentLength = -1, IReadOnlyList<string>? encodings = null, TimeSpan? delay = null) : IModulePackageTransport
{
    public int Calls { get; private set; }
    public async Task<ModulePackageTransportResponse> OpenReadAsync(ModuleUpdatePackage package, CancellationToken token)
    { Calls++; if (delay is { } wait) await Task.Delay(wait, token); return new(new MemoryStream(bytes, false), contentLength == -1 ? bytes.LongLength : contentLength, encodings ?? []); }
}
sealed class FakeClock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 7, 21, 0, 0, 0, TimeSpan.Zero); }
sealed class RedirectHandler(byte[] data, bool trusted) : HttpMessageHandler
{
    public List<Uri> Urls { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Urls.Add(request.RequestUri!);
        if (Urls.Count == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
        { Headers = { Location = new Uri(trusted ? "https://release-assets.githubusercontent.com/asset?sig=x" : "https://example.com/asset") } });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
    }
}
sealed class CancellableTransport(byte[] data) : IModulePackageTransport
{
    private int _calls;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<ModulePackageTransportResponse> OpenReadAsync(ModuleUpdatePackage package, CancellationToken token)
    {
        if (Interlocked.Increment(ref _calls) == 1) { Entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        return new(new MemoryStream(data, false), data.LongLength, []);
    }
}
sealed class MappedChecker(IEnumerable<ModulePackageDownloadRequest> requests) : IModuleUpdateChecker
{
    private readonly Dictionary<string, ModulePackageDownloadRequest> _requests = requests.ToDictionary(request => request.ModuleId);
    public Task<ModuleUpdateResult> CheckModuleAsync(InstalledModuleVersion module, bool manual, CancellationToken token)
    {
        var request = _requests[module.ModuleId];
        var release = new ModuleUpdateRelease(request.TargetVersion, ModuleUpdateChannel.Stable, ModuleUpdateIdentity.ModuleApiVersion,
            SemanticVersion.Parse("0.1.0-alpha"), null, DateTimeOffset.UnixEpoch, request.Package, new Dictionary<string, string>());
        return Task.FromResult(new ModuleUpdateResult(module.ModuleId, ModuleUpdateStatus.UpdateAvailable, release));
    }
    public Task<IReadOnlyDictionary<string, ModuleUpdateResult>> CheckAllInstalledModulesAsync(IReadOnlyCollection<InstalledModuleVersion> modules, bool manual, CancellationToken token) => throw new NotSupportedException();
}
sealed class TrackingTransport : IModulePackageTransport
{
    private int _active;
    public int MaxActive { get; private set; }
    public TaskCompletionSource TwoEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<ModulePackageTransportResponse> OpenReadAsync(ModuleUpdatePackage package, CancellationToken token)
    {
        var active = Interlocked.Increment(ref _active); MaxActive = Math.Max(MaxActive, active); if (active == 2) TwoEntered.SetResult();
        try { await Release.Task.WaitAsync(token); var value = byte.Parse(package.FileName[4].ToString()); return new(new MemoryStream([value], false), 1, []); }
        finally { Interlocked.Decrement(ref _active); }
    }
}
