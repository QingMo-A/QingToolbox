using System.Net;
using System.Text;
using QingToolbox.Core.Updates;

await Smoke.RunAsync();

static class Smoke
{
    public static async Task RunAsync()
    {
        TestSelection(); TestAssets(); await TestHttpAndCacheAsync(); await TestFailuresAsync(); await TestConcurrencyAndIsolationAsync();
        Console.WriteLine("Host update discovery smoke test passed: SemVer channels, installer-only assets, conditional caching, failures and isolation.");
    }

    private static void TestSelection()
    {
        Assert(Best("0.2.0-alpha", R("0.2.1-alpha")) == "0.2.1-alpha", "0.2.0 candidate upgrade selected");
        Assert(Best("0.2.1-alpha", R("0.2.1-alpha"), R("0.2.0-alpha")) is null, "same and lower candidate ignored");
        Assert(Best("1.0.0-alpha", R("1.0.1-alpha")) == "1.0.1-alpha", "alpha to alpha");
        Assert(Best("1.0.0-alpha", R("1.0.0-beta")) == "1.0.0-beta", "alpha to beta");
        Assert(Best("1.0.0-beta", R("1.1.0-alpha")) is null, "beta rejects alpha");
        Assert(Best("1.0.0-rc.1", R("1.0.0")) == "1.0.0", "rc to stable");
        Assert(Best("1.0.0", R("1.1.0-beta")) is null, "stable rejects prerelease");
        Assert(Best("1.0.0", R("1.0.0"), R("0.9.0"), R("invalid"), R("vv9.0.0")) is null, "same lower invalid ignored");
        Assert(Best("1.0.0-alpha", R("1.1.0-alpha"), R("1.3.0-alpha"), R("1.2.0")) == "1.3.0-alpha", "highest selected");
        Assert(Best("1.0.0-alpha", R("9.0.0", draft: true), R("1.1.0-alpha")) == "1.1.0-alpha", "draft ignored");
    }

    private static void TestAssets()
    {
        Assert(Best("1.0.0-alpha", R("1.1.0-alpha")) is not null, "valid asset pair");
        Assert(Best("1.0.0-alpha", R("1.1.0-alpha", assets: ["installer-only"])) is null, "installer missing");
        Assert(Best("1.0.0-alpha", R("1.1.0-alpha", assets: ["QingToolbox-1.1.0-alpha-win-x64-setup.exe"])) is null, "sidecar missing");
        Assert(Best("1.0.0-alpha", R("1.1.0-alpha", assets: ["QingToolbox-1.1.0-alpha-win-x64-setup.exe", "QingToolbox-1.1.0-alpha-win-x64-setup.exe", "QingToolbox-1.1.0-alpha-win-x64-setup.exe.sha256"])) is null, "duplicate installer");
        Assert(Best("1.0.0-alpha", R("1.1.0-alpha", assets: ["QingToolbox-2.0.0-win-x64-setup.exe", "QingToolbox-2.0.0-win-x64-setup.exe.sha256"])) is null, "filename mismatch");
        Assert(Best("1.0.0-alpha", BadAsset("1.1.0-alpha", x => x with { BrowserDownloadUrl = null })) is null, "missing asset URL");
        Assert(Best("1.0.0-alpha", BadAsset("1.1.0-alpha", x => x with { BrowserDownloadUrl = "http://github.com/file" })) is null, "non-HTTPS asset URL");
        Assert(Best("1.0.0-alpha", BadAsset("1.1.0-alpha", x => x with { Size = 0 })) is null, "zero asset size");
        Assert(Best("1.0.0-alpha", BadAsset("1.1.0-alpha", x => x with { Size = HostUpdateInstallerDownloader.MaximumInstallerBytes + 1 })) is null, "oversize installer");
    }

    private static async Task TestHttpAndCacheAsync()
    {
        await InTemp(async root =>
        {
            var clock = new Clock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
            var handler = new Handler((request, call) => call == 1
                ? Response(HttpStatusCode.OK, Json("1.1.0-alpha"), "\"release-v1\"", DateTimeOffset.Parse("2025-12-31T00:00:00Z"))
                : new HttpResponseMessage(HttpStatusCode.NotModified));
            var service = Service(handler, root, clock);
            var first = await service.CheckAsync(false);
            Assert(first.State == HostUpdateCheckState.UpdateAvailable && handler.Calls == 1, "200 update");
            Assert(handler.Last!.Headers.UserAgent.Count > 0 && handler.Last.Headers.Accept.Any(x => x.MediaType == "application/vnd.github+json"), "headers");
            Assert((await service.CheckAsync(false)).FromCache && handler.Calls == 1, "24 hour skip");
            clock.Now += TimeSpan.FromHours(25);
            var second = await service.CheckAsync(false);
            Assert(second.State == HostUpdateCheckState.UpdateAvailable && handler.Calls == 2, "304 cache");
            Assert(handler.Last.Headers.IfNoneMatch.Any(x => x.Tag == "\"release-v1\"") && handler.Last.Headers.IfModifiedSince.HasValue, "conditional headers");
            await service.CheckAsync(true);
            Assert(handler.Calls == 3, "manual bypass");
        });
    }

    private static async Task TestFailuresAsync()
    {
        foreach (var response in new Func<HttpResponseMessage>[] {
            () => new(HttpStatusCode.InternalServerError),
            () => new(HttpStatusCode.OK) { Content = new StringContent("not-json") },
            () => throw new TaskCanceledException() })
        {
            var service = Service(new Handler((_, _) => response()), null, new Clock(DateTimeOffset.UtcNow));
            Assert((await service.CheckAsync(true)).State == HostUpdateCheckState.Failed, "safe failure");
        }
        await InTemp(async root =>
        {
            await File.WriteAllTextAsync(Path.Combine(root, "cache.json"), "broken");
            var handler = new Handler((_, _) => Response(HttpStatusCode.OK, "[]"));
            Assert((await Service(handler, root, new Clock(DateTimeOffset.UtcNow)).CheckAsync(false)).State == HostUpdateCheckState.UpToDate, "corrupt cache fallback");
        });
    }

    private static async Task TestConcurrencyAndIsolationAsync()
    {
        var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelayedHandler(gate.Task);
        var service = Service(handler, null, new Clock(DateTimeOffset.UtcNow));
        var one = service.CheckAsync(true); var two = service.CheckAsync(true);
        await Task.Delay(50); Assert(handler.Calls == 1, "concurrent coalescing");
        gate.SetResult(Response(HttpStatusCode.OK, "[]")); await Task.WhenAll(one, two);
        var disabledHandler = new Handler((_, _) => throw new Exception("network used"));
        var disabled = new HostUpdateDiscoveryService(new HttpClient(disabledHandler), null, SemanticVersion.Parse("1.0.0"), TimeProvider.System, false);
        Assert((await disabled.CheckAsync(true)).State == HostUpdateCheckState.Idle && disabledHandler.Calls == 0, "non-production disabled");
    }

    private static HostUpdateDiscoveryService Service(HttpMessageHandler handler, string? root, TimeProvider clock) =>
        new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) }, root is null ? null : Path.Combine(root, "cache.json"), SemanticVersion.Parse("1.0.0-alpha"), clock, true);
    private static string? Best(string current, params HostUpdateDiscoveryService.ReleaseRecord[] releases) => HostUpdateDiscoveryService.SelectBestRelease(releases, SemanticVersion.Parse(current))?.Version;
    private static HostUpdateDiscoveryService.ReleaseRecord R(string version, bool draft = false, string[]? assets = null)
    {
        assets ??= [$"QingToolbox-{version}-win-x64-setup.exe", $"QingToolbox-{version}-win-x64-setup.exe.sha256"];
        return new(version, draft, DateTimeOffset.UtcNow, "# Notes\nSafe **summary**", assets.Select((x, i) =>
            new HostUpdateDiscoveryService.ReleaseAssetRecord(i + 1, x, $"https://github.com/QingMo-A/QingToolbox/releases/download/v{version}/{x}", x.EndsWith(".sha256", StringComparison.Ordinal) ? 100 : 1024)).ToArray());
    }
    private static HostUpdateDiscoveryService.ReleaseRecord BadAsset(string version, Func<HostUpdateDiscoveryService.ReleaseAssetRecord, HostUpdateDiscoveryService.ReleaseAssetRecord> change)
    {
        var release = R(version); var assets = release.Assets.ToArray(); assets[0] = change(assets[0]); return release with { Assets = assets };
    }
    private static string Json(string version) => $"[{{\"tag_name\":\"{version}\",\"draft\":false,\"published_at\":\"2025-12-01T00:00:00Z\",\"body\":\"Notes\",\"assets\":[{{\"id\":1,\"name\":\"QingToolbox-{version}-win-x64-setup.exe\",\"browser_download_url\":\"https://github.com/QingMo-A/QingToolbox/releases/download/v{version}/QingToolbox-{version}-win-x64-setup.exe\",\"size\":1024}},{{\"id\":2,\"name\":\"QingToolbox-{version}-win-x64-setup.exe.sha256\",\"browser_download_url\":\"https://github.com/QingMo-A/QingToolbox/releases/download/v{version}/QingToolbox-{version}-win-x64-setup.exe.sha256\",\"size\":100}}]}}]";
    private static HttpResponseMessage Response(HttpStatusCode status, string json = "[]", string? etag = null, DateTimeOffset? modified = null)
    { var r = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }; if (etag is not null) r.Headers.ETag = new(etag); if (modified.HasValue) r.Content.Headers.LastModified = modified; return r; }
    private static async Task InTemp(Func<string, Task> action) { var root = Path.Combine(Path.GetTempPath(), "QingToolbox-host-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { await action(root); } finally { Directory.Delete(root, true); } }
    private static void Assert(bool condition, string name) { if (!condition) throw new InvalidOperationException("Failed: " + name); }
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> response) : HttpMessageHandler { public int Calls; public HttpRequestMessage? Last; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; Last = request; return Task.FromResult(response(request, Calls)); } }
    private sealed class DelayedHandler(Task<HttpResponseMessage> response) : HttpMessageHandler { public int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return response.WaitAsync(cancellationToken); } }
    private sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
}
