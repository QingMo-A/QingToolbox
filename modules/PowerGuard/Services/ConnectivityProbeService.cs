using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using QingToolbox.Modules.PowerGuard.Models;

namespace QingToolbox.Modules.PowerGuard.Services;

public interface IConnectivityProbe { Task<ConnectivityProbeResult> ProbeAsync(CancellationToken token = default); }

public sealed class ConnectivityProbeService : IConnectivityProbe, IDisposable
{
    private readonly HttpClient _client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly (string Name, Uri Uri, string Expected)[] Targets =
    [
        ("Microsoft", new Uri("https://www.msftconnecttest.com/connecttest.txt"), "Microsoft Connect Test"),
        ("Cloudflare", new Uri("https://cp.cloudflare.com/generate_204"), "")
    ];

    public async Task<ConnectivityProbeResult> ProbeAsync(CancellationToken token = default)
    {
        var available = NetworkInterface.GetIsNetworkAvailable();
        var results = new List<ProbeEndpointResult>();
        foreach (var target in Targets)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                using var response = await _client.GetAsync(target.Uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var body = response.StatusCode == System.Net.HttpStatusCode.NoContent
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync(timeout.Token);
                var valid = response.IsSuccessStatusCode &&
                    (target.Expected.Length == 0 ? response.StatusCode == System.Net.HttpStatusCode.NoContent : body.Trim() == target.Expected);
                results.Add(new(target.Name, valid, stopwatch.ElapsedMilliseconds, valid ? "" : "UnexpectedResponse"));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                results.Add(new(target.Name, false, stopwatch.ElapsedMilliseconds, exception.GetType().Name));
            }
        }
        return new(results.Any(x => x.Succeeded), available, results, DateTimeOffset.UtcNow);
    }
    public void Dispose() => _client.Dispose();
}
