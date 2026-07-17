using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using QingToolbox.Modules.PowerGuard.Models;

namespace QingToolbox.Modules.PowerGuard.Services;

public interface IConnectivityProbe { Task<ConnectivityProbeResult> ProbeAsync(CancellationToken token = default); }
public sealed record ConnectivityTarget(
    string Name,
    Uri Uri,
    HttpStatusCode Status,
    string? ExpectedText,
    string Region = "Custom");

public sealed class ConnectivityProbeService : IConnectivityProbe, IDisposable
{
    private const int MaximumBodyBytes=1024;
    private readonly HttpClient _client;
    private readonly IReadOnlyList<ConnectivityTarget> _targets;
    private readonly TimeSpan _budget;
    private readonly TimeProvider _timeProvider;
    public ConnectivityProbeService(HttpMessageHandler? handler=null,IReadOnlyList<ConnectivityTarget>? targets=null,TimeSpan? budget=null,TimeProvider? timeProvider=null)
    {
        handler??=new HttpClientHandler{AllowAutoRedirect=false};
        _client=new(handler,true){Timeout=Timeout.InfiniteTimeSpan};
        _targets=targets??[
            new("Microsoft",new("https://www.msftconnecttest.com/connecttest.txt"),HttpStatusCode.OK,"Microsoft Connect Test","Global"),
            new("Cloudflare",new("https://cp.cloudflare.com/generate_204"),HttpStatusCode.NoContent,null,"Global"),
            new("Xiaomi",new("https://connect.rom.miui.com/generate_204"),HttpStatusCode.NoContent,null,"China"),
            new("Vivo",new("https://wifi.vivo.com.cn/generate_204"),HttpStatusCode.NoContent,null,"China")];
        _budget=budget??TimeSpan.FromSeconds(6);
        _timeProvider=timeProvider??TimeProvider.System;
    }
    public async Task<ConnectivityProbeResult> ProbeAsync(CancellationToken token=default)
    {
        using var timeout=new CancellationTokenSource(_budget,_timeProvider);using var budget=CancellationTokenSource.CreateLinkedTokenSource(token,timeout.Token);
        var tasks=_targets.Select(target=>ProbeTargetAsync(target,budget.Token,token)).ToArray();
        var results=await Task.WhenAll(tasks);
        token.ThrowIfCancellationRequested();
        // Endpoint success is authoritative. Link state is diagnostic evidence only:
        // a router can keep Ethernet up while its WAN is down, and VPN/virtual adapters
        // can make the Windows aggregate state look available after a cable is removed.
        return new(results.Any(x=>x.Succeeded),HasUsablePhysicalLink(),results,_timeProvider.GetUtcNow());
    }
    private static bool HasUsablePhysicalLink()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(networkInterface =>
                networkInterface.OperationalStatus == OperationalStatus.Up &&
                networkInterface.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Wireless80211 &&
                !networkInterface.IsReceiveOnly &&
                networkInterface.GetIPProperties().UnicastAddresses.Any(address =>
                    address.Address.AddressFamily is System.Net.Sockets.AddressFamily.InterNetwork or System.Net.Sockets.AddressFamily.InterNetworkV6));
        }
        catch (NetworkInformationException)
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
    }
    private async Task<ProbeEndpointResult> ProbeTargetAsync(ConnectivityTarget target,CancellationToken budget,CancellationToken caller)
    {
        var sw=Stopwatch.StartNew();
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,target.Uri);
            using var response=await _client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,budget);
            if((int)response.StatusCode is >=300 and <400)return new(target.Name,false,sw.ElapsedMilliseconds,"RedirectRejected",target.Region);
            if(response.StatusCode!=target.Status)return new(target.Name,false,sw.ElapsedMilliseconds,"UnexpectedStatus",target.Region);
            if(target.ExpectedText is null)return new(target.Name,true,sw.ElapsedMilliseconds,"",target.Region);
            if(response.Content.Headers.ContentLength>MaximumBodyBytes)return new(target.Name,false,sw.ElapsedMilliseconds,"ResponseTooLarge",target.Region);
            await using var stream=await response.Content.ReadAsStreamAsync(budget);
            var buffer=new byte[MaximumBodyBytes+1];var read=0;
            while(read<buffer.Length){var count=await stream.ReadAsync(buffer.AsMemory(read,buffer.Length-read),budget);if(count==0)break;read+=count;}
            if(read>MaximumBodyBytes)return new(target.Name,false,sw.ElapsedMilliseconds,"ResponseTooLarge",target.Region);
            var text=System.Text.Encoding.UTF8.GetString(buffer,0,read).Trim();
            return new(target.Name,text==target.ExpectedText,sw.ElapsedMilliseconds,text==target.ExpectedText?"":"UnexpectedResponse",target.Region);
        }
        catch(OperationCanceledException) when(caller.IsCancellationRequested){throw;}
        catch(OperationCanceledException){return new(target.Name,false,sw.ElapsedMilliseconds,"Timeout",target.Region);}
        catch(Exception exception){return new(target.Name,false,sw.ElapsedMilliseconds,exception.GetType().Name,target.Region);}
    }
    public void Dispose()=>_client.Dispose();
}
