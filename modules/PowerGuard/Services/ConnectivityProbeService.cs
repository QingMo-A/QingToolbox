using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using QingToolbox.Modules.PowerGuard.Models;

namespace QingToolbox.Modules.PowerGuard.Services;

public interface IConnectivityProbe { Task<ConnectivityProbeResult> ProbeAsync(CancellationToken token = default); }
public sealed record ConnectivityTarget(string Name, Uri Uri, HttpStatusCode Status, string? ExpectedText);

public sealed class ConnectivityProbeService : IConnectivityProbe, IDisposable
{
    private const int MaximumBodyBytes=1024;
    private readonly HttpClient _client;
    private readonly IReadOnlyList<ConnectivityTarget> _targets;
    public ConnectivityProbeService(HttpMessageHandler? handler=null,IReadOnlyList<ConnectivityTarget>? targets=null)
    {
        handler??=new HttpClientHandler{AllowAutoRedirect=false};
        _client=new(handler,true){Timeout=Timeout.InfiniteTimeSpan};
        _targets=targets??[
            new("Microsoft",new("https://www.msftconnecttest.com/connecttest.txt"),HttpStatusCode.OK,"Microsoft Connect Test"),
            new("Cloudflare",new("https://cp.cloudflare.com/generate_204"),HttpStatusCode.NoContent,null)];
    }
    public async Task<ConnectivityProbeResult> ProbeAsync(CancellationToken token=default)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(token);budget.CancelAfter(TimeSpan.FromSeconds(5));
        var tasks=_targets.Select(target=>ProbeTargetAsync(target,budget.Token,token)).ToArray();
        var results=await Task.WhenAll(tasks);
        token.ThrowIfCancellationRequested();
        return new(results.Any(x=>x.Succeeded),NetworkInterface.GetIsNetworkAvailable(),results,DateTimeOffset.UtcNow);
    }
    private async Task<ProbeEndpointResult> ProbeTargetAsync(ConnectivityTarget target,CancellationToken budget,CancellationToken caller)
    {
        var sw=Stopwatch.StartNew();
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,target.Uri);
            using var response=await _client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,budget);
            if((int)response.StatusCode is >=300 and <400)return new(target.Name,false,sw.ElapsedMilliseconds,"RedirectRejected");
            if(response.StatusCode!=target.Status)return new(target.Name,false,sw.ElapsedMilliseconds,"UnexpectedStatus");
            if(target.ExpectedText is null)return new(target.Name,true,sw.ElapsedMilliseconds,"");
            if(response.Content.Headers.ContentLength>MaximumBodyBytes)return new(target.Name,false,sw.ElapsedMilliseconds,"ResponseTooLarge");
            await using var stream=await response.Content.ReadAsStreamAsync(budget);
            var buffer=new byte[MaximumBodyBytes+1];var read=0;
            while(read<buffer.Length){var count=await stream.ReadAsync(buffer.AsMemory(read,buffer.Length-read),budget);if(count==0)break;read+=count;}
            if(read>MaximumBodyBytes)return new(target.Name,false,sw.ElapsedMilliseconds,"ResponseTooLarge");
            var text=System.Text.Encoding.UTF8.GetString(buffer,0,read).Trim();
            return new(target.Name,text==target.ExpectedText,sw.ElapsedMilliseconds,text==target.ExpectedText?"":"UnexpectedResponse");
        }
        catch(OperationCanceledException) when(caller.IsCancellationRequested){throw;}
        catch(OperationCanceledException){return new(target.Name,false,sw.ElapsedMilliseconds,"Timeout");}
        catch(Exception exception){return new(target.Name,false,sw.ElapsedMilliseconds,exception.GetType().Name);}
    }
    public void Dispose()=>_client.Dispose();
}
