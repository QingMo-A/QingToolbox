using System.Net;
using System.Text;
using QingToolbox.Core.Updates;

await Smoke.RunAsync();

static class Smoke
{
    public static async Task RunAsync()
    {
        TestSemVer(); TestProtocol(); TestCompatibility(); await TestHttpAndCacheAsync(); await TestCoordinatorAsync(); await TestModuleTestAsync();
        Console.WriteLine("Module update smoke test passed: strict protocol, SemVer, compatibility, conditional cache and network isolation.");
    }
    private static void TestSemVer()
    {
        Assert(SemanticVersion.Parse("1.0.0").CompareTo(SemanticVersion.Parse("1.0.0-alpha")) > 0, "stable precedence");
        Assert(SemanticVersion.Parse("1.0.0-alpha.10").CompareTo(SemanticVersion.Parse("1.0.0-alpha.2")) > 0, "numeric prerelease");
        Assert(SemanticVersion.Parse("1.0.0-1").CompareTo(SemanticVersion.Parse("1.0.0-alpha")) < 0, "numeric below text");
        Assert(SemanticVersion.Parse("1.0.0+one").CompareTo(SemanticVersion.Parse("1.0.0+two")) == 0, "build ignored");
        Assert(!SemanticVersion.TryParse("01.0.0", out _) && !SemanticVersion.TryParse("v1.0.0", out _) && !SemanticVersion.TryParse("1.0", out _), "invalid versions");
    }
    private static void TestProtocol()
    {
        var index = Bytes("""{"schemaVersion":1,"sourceId":"qingtoolbox-official","modules":{"qing.demo":{"updateManifest":"Demo/update.json"}}}""");
        Assert(ModuleUpdateProtocolParser.ParseIndex(index).Modules.Count == 1, "index");
        var update = Bytes(Update("[]")); Assert(ModuleUpdateProtocolParser.ParseUpdate(update, "qing.demo").Releases.Count == 0, "empty releases");
        Reject(() => ModuleUpdateProtocolParser.ParseIndex(Bytes("""{"schemaVersion":1,"schemaVersion":1,"sourceId":"qingtoolbox-official","modules":{}}""")), "duplicate");
        Reject(() => ModuleUpdateProtocolParser.ParseIndex(Bytes("""{"schemaVersion":1,"sourceId":"qingtoolbox-official","modules":{"qing.demo":{"updateManifest":"../update.json"}}}""")), "traversal");
        foreach (var path in new[] { "PowerGuard/%2e%2e/Evil/update.json", "PowerGuard/%2F/update.json", "PowerGuard/%5C/update.json",
            "PowerGuard//update.json", "PowerGuard /update.json", "PowerGuard:Test/update.json", "PowerGuard\\update.json", "模块/update.json" })
            Reject(() => ModuleUpdateProtocolParser.ParseIndex(Bytes(Index(path))), "encoded path " + path);
        Reject(() => ModuleUpdateProtocolParser.ParseIndex(new byte[256 * 1024 + 1]), "large index");
        Reject(() => ModuleUpdateProtocolParser.ParseUpdate(new byte[128 * 1024 + 1], "qing.demo"), "large update");
        var release = """[{"version":"0.2.0-alpha","channel":"preview","moduleApiVersion":"experimental-0.1","minimumHostVersion":"0.1.0-alpha","maximumHostVersionExclusive":"1.0.0","publishedAt":"2026-07-20T08:00:00Z","package":{"fileName":"Demo-0.2.0-alpha.qmod","url":"https://github.com/QingMo-A/QingToolbox/releases/download/v0.2.0-alpha/Demo-0.2.0-alpha.qmod","size":42,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"releaseNotes":{"zh-CN":"说明","en-US":"Notes"}}]""";
        Assert(ModuleUpdateProtocolParser.ParseUpdate(Bytes(Update(release)), "qing.demo").Releases.Count == 1, "release");
        Assert(ModuleUpdateProtocolParser.ParseUpdate(Bytes(Update(release.Replace("08:00:00Z", "08:00:00.1234567Z"))), "qing.demo").Releases.Count == 1, "fractional UTC");
        foreach (var timestamp in new[] { "2026-07-20T08:00:00+00:00", "2026-07-20 08:00:00Z", "2026-7-20T8:00:00Z", " 2026-07-20T08:00:00Z" })
            Reject(() => ModuleUpdateProtocolParser.ParseUpdate(Bytes(Update(release.Replace("2026-07-20T08:00:00Z", timestamp))), "qing.demo"), "strict UTC " + timestamp);
        Reject(() => ModuleUpdateProtocolParser.ParseUpdate(Bytes(Update(release.Replace("/releases/download/", "/releases/latest/download/"))), "qing.demo"), "mutable URL");
    }
    private static void TestCompatibility()
    {
        var manifest = ModuleUpdateProtocolParser.ParseUpdate(Bytes(Update("""[{"version":"0.2.0","channel":"stable","moduleApiVersion":"experimental-0.1","minimumHostVersion":"0.1.0-alpha","publishedAt":"2026-07-20T08:00:00Z","package":{"fileName":"Demo.qmod","url":"https://github.com/QingMo-A/QingToolbox/releases/download/v0.2.0/Demo.qmod","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"releaseNotes":{"zh-CN":"说明","en-US":"Notes"}}]""")), "qing.demo");
        var evaluator = new ModuleUpdateCompatibilityEvaluator(SemanticVersion.Parse("0.1.0-alpha"), "experimental-0.1", ModuleUpdateChannel.Preview);
        Assert(evaluator.Evaluate("qing.demo", "0.1.0", manifest, DateTimeOffset.UtcNow).Status == ModuleUpdateStatus.UpdateAvailable, "available");
        Assert(evaluator.Evaluate("qing.demo", "0.2.0", manifest, DateTimeOffset.UtcNow).Status == ModuleUpdateStatus.UpToDate, "current");
        Assert(evaluator.Evaluate("qing.demo", "bad", manifest, DateTimeOffset.UtcNow).Status == ModuleUpdateStatus.InvalidLocalVersion, "invalid local");
        var upperManifest = ModuleUpdateProtocolParser.ParseUpdate(Bytes(Update("""[{"version":"0.3.0","channel":"stable","moduleApiVersion":"experimental-0.1","minimumHostVersion":"0.1.0-alpha","maximumHostVersionExclusive":"0.2.0","publishedAt":"2026-07-20T08:00:00Z","package":{"fileName":"Demo.qmod","url":"https://github.com/QingMo-A/QingToolbox/releases/download/v0.3.0/Demo.qmod","size":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"releaseNotes":{"zh-CN":"说明","en-US":"Notes"}}]""")), "qing.demo");
        var modernHost = new ModuleUpdateCompatibilityEvaluator(SemanticVersion.Parse("0.2.0"), "experimental-0.1", ModuleUpdateChannel.Stable);
        Assert(modernHost.Evaluate("qing.demo", "0.1.0", upperManifest, DateTimeOffset.UtcNow).Status == ModuleUpdateStatus.HostVersionIncompatible, "maximum host bound");
    }
    private static async Task TestHttpAndCacheAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "QingToolbox-update-smoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero)); var handler = new FakeHandler();
            handler.Enqueue(HttpStatusCode.OK, """{"schemaVersion":1,"sourceId":"qingtoolbox-official","modules":{"qing.demo":{"updateManifest":"Demo/update.json"}}}""", "\"index-v1\"");
            handler.Enqueue(HttpStatusCode.OK, Update("[]"), "\"module-v1\"");
            handler.Enqueue(HttpStatusCode.NotModified, "", null);
            var source = new OfficialModuleUpdateSource(new HttpClient(handler), new ModuleUpdateCache(root, clock), clock);
            var evaluator = new ModuleUpdateCompatibilityEvaluator(SemanticVersion.Parse("0.1.0-alpha"), "experimental-0.1", ModuleUpdateChannel.Preview);
            var checker = new ModuleUpdateChecker(source, evaluator, TimeProvider.System, false);
            var installed = new[] { new InstalledModuleVersion("qing.demo", "0.1.0") };
            var first = await checker.CheckAllInstalledModulesAsync(installed, true, default);
            Assert(first["qing.demo"].Status == ModuleUpdateStatus.NoPublishedRelease, "first 200");
            await source.GetIndexAsync(true, default);
            Assert(handler.Requests.Count == 3 && handler.Requests[2].Headers.IfNoneMatch.Any(), "ETag conditional request");
            clock.Advance(TimeSpan.FromHours(1));
            await source.GetIndexAsync(false, default);
            Assert(handler.Requests.Count == 3, "304 refreshes 24-hour freshness");
            Assert(Directory.GetFiles(root, "*.cache.json").Length == 2 && Directory.GetFiles(root, "*.meta").Length == 0, "single envelope cache");
            Assert(handler.Requests.All(x => !x.RequestUri!.AbsolutePath.EndsWith(".qmod", StringComparison.OrdinalIgnoreCase)), "no qmod request");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
    private static async Task TestCoordinatorAsync()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); var source = new SequencedSource();
        var checker = new ModuleUpdateChecker(source, new(SemanticVersion.Parse("0.1.0-alpha"), "experimental-0.1", ModuleUpdateChannel.Preview), clock, false);
        await using var coordinator = new ModuleUpdateCheckCoordinator(checker, clock);
        var empty = await coordinator.CheckAsync(new([], false, clock.GetUtcNow()));
        Assert(empty.Results.Count == 0 && source.Calls == 0, "empty coordinator does not call source");
        var request = new[] { new InstalledModuleVersion("qing.demo", "0.1.0") };
        var automatic = coordinator.CheckAsync(new(request, false, clock.GetUtcNow()));
        await source.FirstEntered.Task;
        var manual = coordinator.CheckAsync(new(request, true, clock.GetUtcNow()));
        source.ReleaseFirst.SetResult();
        await automatic; await manual;
        Assert(source.Calls == 2 && source.MaxConcurrent == 1, "automatic and manual serialized with real manual follow-up");
    }
    private static async Task TestModuleTestAsync()
    {
        var fake = new NeverSource(); var checker = new ModuleUpdateChecker(fake,
            new(SemanticVersion.Parse("0.1.0-alpha"), "experimental-0.1", ModuleUpdateChannel.Preview), TimeProvider.System, true);
        var result = await checker.CheckAllInstalledModulesAsync([new("qing.demo", "0.1.0")], true, default);
        Assert(result["qing.demo"].Status == ModuleUpdateStatus.DisabledByEnvironment && fake.Calls == 0, "ModuleTest network disabled");
    }
    private static string Update(string releases) => $$"""{"schemaVersion":1,"moduleId":"qing.demo","publisher":"QingMo-A","releases":{{releases}}}""";
    private static string Index(string path) => "{\"schemaVersion\":1,\"sourceId\":\"qingtoolbox-official\",\"modules\":{\"qing.demo\":{\"updateManifest\":\"" + path.Replace("\\", "\\\\") + "\"}}}";
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static void Reject(Action action, string name) { try { action(); throw new Exception("Expected rejection: " + name); } catch (ModuleUpdateProtocolException) { } }
    private static void Assert(bool condition, string name) { if (!condition) throw new Exception("Assertion failed: " + name); }
}

sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan value) => _now += value;
}

sealed class SequencedSource : IModuleUpdateSource
{
    private int _active;
    public int Calls { get; private set; }
    public int MaxConcurrent { get; private set; }
    public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<ModuleUpdateSourceResponse<OfficialModuleIndex>> GetIndexAsync(bool manual, CancellationToken token)
    {
        Calls++; var active = Interlocked.Increment(ref _active); MaxConcurrent = Math.Max(MaxConcurrent, active);
        try { if (Calls == 1) { FirstEntered.SetResult(); await ReleaseFirst.Task.WaitAsync(token); } return new(new OfficialModuleIndex(new Dictionary<string,string>()), false, DateTimeOffset.UtcNow); }
        finally { Interlocked.Decrement(ref _active); }
    }
    public Task<ModuleUpdateSourceResponse<ModuleUpdateManifest>> GetManifestAsync(string id, string path, bool manual, CancellationToken token) => throw new InvalidOperationException();
}

sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<(HttpStatusCode Status, string Body, string? ETag)> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = [];
    public void Enqueue(HttpStatusCode status, string body, string? etag) => _responses.Enqueue((status, body, etag));
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri); foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value); Requests.Add(clone);
        var next = _responses.Dequeue(); var response = new HttpResponseMessage(next.Status) { Content = new StringContent(next.Body), RequestMessage = request };
        if (next.ETag is not null) response.Headers.TryAddWithoutValidation("ETag", next.ETag); return Task.FromResult(response);
    }
}

sealed class NeverSource : IModuleUpdateSource
{
    public int Calls { get; private set; }
    public Task<ModuleUpdateSourceResponse<OfficialModuleIndex>> GetIndexAsync(bool manual, CancellationToken token) { Calls++; throw new Exception(); }
    public Task<ModuleUpdateSourceResponse<ModuleUpdateManifest>> GetManifestAsync(string id, string path, bool manual, CancellationToken token) { Calls++; throw new Exception(); }
}
