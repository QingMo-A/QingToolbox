using System.Net;
using System.Security.Cryptography;
using System.Text;
using QingToolbox.Core.Updates;

await Smoke.RunAsync();

static class Smoke
{
    public static async Task RunAsync()
    {
        TestSidecars(); await TestDownloadAsync(); await TestFailuresAsync(); await TestCacheAndConcurrencyAsync();
        Console.WriteLine("Host update download smoke test passed: strict sidecars, bounded streaming, cancellation, hashing, cache reuse and coalescing.");
    }

    private static void TestSidecars()
    {
        var name = "QingToolbox-0.3.0-alpha-win-x64-setup.exe"; var hash = new string('A', 64);
        Assert(HostUpdateInstallerDownloader.ParseSidecar(Utf8($"{hash}  {name}\n"), name) == hash, "uppercase sidecar");
        Assert(HostUpdateInstallerDownloader.ParseSidecar(Utf8($"{hash.ToLowerInvariant()}  {name}"), name) == hash, "lowercase sidecar");
        foreach (var invalid in new[] { $"{hash[..63]}  {name}", $"{hash} {name}", $"{hash}  other.exe", $"{hash}  ..\\{name}", $"{hash}  {name}\n{hash}  {name}", $"{new string('Z',64)}  {name}" })
            Throws(() => HostUpdateInstallerDownloader.ParseSidecar(Utf8(invalid), name), "invalid sidecar");
        Throws(() => HostUpdateInstallerDownloader.ParseSidecar(new byte[4097], name), "oversize sidecar");
    }

    private static async Task TestDownloadAsync()
    {
        await InTemp(async root =>
        {
            var payload = Enumerable.Range(0, 180_000).Select(x => (byte)(x % 251)).ToArray();
            var release = Release(payload); var handler = new QueueHandler(Response(Sidecar(payload, release)), Response(payload));
            var downloader = new HostUpdateInstallerDownloader(new HttpClient(handler), root); var progress = new List<long>();
            downloader.ProgressChanged += (_, x) => { if (x.State == HostUpdateDownloadState.Downloading) progress.Add(x.BytesReceived); };
            var result = await downloader.DownloadAsync(release, CancellationToken.None);
            Assert(result.State == HostUpdateDownloadState.ReadyToInstall && File.Exists(result.InstallerPath), "successful atomic commit");
            Assert(!File.Exists(result.InstallerPath + ".part") && progress.Count > 1 && progress[^1] == payload.Length, "real streaming progress");
        });
        await InTemp(async root =>
        {
            var payload = Utf8("download without content length"); var release = Release(payload);
            var result = await new HostUpdateInstallerDownloader(new HttpClient(new QueueHandler(ResponseNoLength(Sidecar(payload, release)), ResponseNoLength(payload))), root)
                .DownloadAsync(release, CancellationToken.None);
            Assert(result.State == HostUpdateDownloadState.ReadyToInstall, "missing Content-Length accepted with exact bounded size");
        });
    }

    private static async Task TestFailuresAsync()
    {
        await InTemp(async root =>
        {
            var payload = Utf8("installer"); var release = Release(payload);
            foreach (var responses in new[] {
                new[] { Response(Utf8(new string('0',64) + "  wrong.exe\n")) },
                new[] { Response(Sidecar(payload, release)), Response(Utf8("wrong")) },
                new[] { Response(Sidecar(payload, release)), ResponseNoLength(payload[..^1]) },
                new[] { Response(Sidecar(payload, release)), ResponseNoLength(payload.Concat(new byte[] { 1 }).ToArray()) },
                new[] { Response(Sidecar(payload, release)), new HttpResponseMessage(HttpStatusCode.InternalServerError) } })
            {
                var result = await new HostUpdateInstallerDownloader(new HttpClient(new QueueHandler(responses)), root).DownloadAsync(release, CancellationToken.None);
                Assert(result.State == HostUpdateDownloadState.Failed && !Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(), "failure cleanup");
            }
        });
        await InTemp(async root =>
        {
            var payload = Utf8("installer"); var release = Release(payload);
            var timedOut = await new HostUpdateInstallerDownloader(new HttpClient(new TimeoutHandler()), root).DownloadAsync(release, CancellationToken.None);
            Assert(timedOut.State == HostUpdateDownloadState.Failed, "timeout is failure");
        });
        await InTemp(async root =>
        {
            var payload = Utf8("installer"); var release = Release(payload);
            var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new("https://example.com/untrusted.exe");
            var rejected = await new HostUpdateInstallerDownloader(new HttpClient(new QueueHandler(redirect)), root).DownloadAsync(release, CancellationToken.None);
            Assert(rejected.State == HostUpdateDownloadState.Failed, "untrusted redirect rejected");
        });
        await InTemp(async root =>
        {
            var payload = new byte[100]; var release = Release(payload); using var cts = new CancellationTokenSource();
            var handler = new CancelHandler(); var downloader = new HostUpdateInstallerDownloader(new HttpClient(handler), root);
            var task = downloader.DownloadAsync(release, cts.Token); await handler.Started.Task; cts.Cancel();
            Assert((await task).State == HostUpdateDownloadState.Idle && !Directory.EnumerateFiles(root, "*.part", SearchOption.AllDirectories).Any(), "cancel cleanup");
        });
    }

    private static async Task TestCacheAndConcurrencyAsync()
    {
        await InTemp(async root =>
        {
            var payload = Utf8("verified installer payload"); var release = Release(payload);
            var handler = new QueueHandler(Response(Sidecar(payload, release)), Response(payload)); var downloader = new HostUpdateInstallerDownloader(new HttpClient(handler), root);
            var one = downloader.DownloadAsync(release, CancellationToken.None); var two = downloader.DownloadAsync(release, CancellationToken.None);
            var results = await Task.WhenAll(one, two); Assert(handler.Calls == 2 && results.All(x => x.State == HostUpdateDownloadState.ReadyToInstall), "coalesced download");
            var noNetwork = new QueueHandler(); var reused = await new HostUpdateInstallerDownloader(new HttpClient(noNetwork), root).DownloadAsync(release, CancellationToken.None);
            Assert(reused.State == HostUpdateDownloadState.ReadyToInstall && noNetwork.Calls == 0, "rehash cache reuse");
            await File.WriteAllTextAsync(reused.InstallerPath!, "tampered");
            var failed = await new HostUpdateInstallerDownloader(new HttpClient(new QueueHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError))), root).DownloadAsync(release, CancellationToken.None);
            Assert(failed.State == HostUpdateDownloadState.Failed, "tampered cache rejected");
        });
    }

    private static HostReleaseInfo Release(byte[] payload)
    {
        const string version = "0.3.0-alpha"; var installer = $"QingToolbox-{version}-win-x64-setup.exe";
        var sidecar = $"{Convert.ToHexString(SHA256.HashData(payload))}  {installer}\n";
        return new(version, DateTimeOffset.UtcNow, "notes",
            new(1, installer, new($"https://github.com/QingMo-A/QingToolbox/releases/download/v{version}/{installer}"), payload.Length),
            new(2, installer + ".sha256", new($"https://github.com/QingMo-A/QingToolbox/releases/download/v{version}/{installer}.sha256"), Utf8(sidecar).Length));
    }
    private static byte[] Sidecar(byte[] payload, HostReleaseInfo release) => Utf8($"{Convert.ToHexString(SHA256.HashData(payload))}  {release.Installer.Name}\n");
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static HttpResponseMessage ResponseNoLength(byte[] bytes) { var content = new StreamContent(new MemoryStream(bytes)); content.Headers.ContentLength = null; return new(HttpStatusCode.OK) { Content = content }; }
    private static async Task InTemp(Func<string, Task> body) { var root = Path.Combine(Path.GetTempPath(), "QingToolbox-host-download-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { await body(root); } finally { Directory.Delete(root, true); } }
    private static void Assert(bool value, string name) { if (!value) throw new InvalidOperationException("Failed: " + name); }
    private static void Throws(Action action, string name) { try { action(); throw new InvalidOperationException("Did not reject: " + name); } catch (InvalidDataException) { } }
    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler { private readonly Queue<HttpResponseMessage> queue = new(responses); public int Calls; protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(queue.Dequeue()); } }
    private sealed class CancelHandler : HttpMessageHandler { public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new InvalidOperationException("Unreachable."); } }
    private sealed class TimeoutHandler : HttpMessageHandler { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout")); }
}
