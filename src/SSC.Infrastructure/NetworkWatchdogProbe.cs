using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SSC.Core;
namespace SSC.Infrastructure;
public sealed class NetworkWatchdogProbe : IWatchdogProbe, IDisposable
{
    private readonly WatchdogOptions _options;
    private readonly HttpClient _http;
    private readonly X509Certificate2 _certificate;
    public NetworkWatchdogProbe(WatchdogOptions options, ISecretStore secrets)
    {
        options.Validate();_options=options;
        _certificate=new X509Certificate2(options.ClientCertificatePath,secrets.Read(options.CertificatePasswordSecret),X509KeyStorageFlags.EphemeralKeySet);
        var handler=new HttpClientHandler { AllowAutoRedirect=false,UseProxy=false,ClientCertificateOptions=ClientCertificateOption.Manual,SslProtocols=SslProtocols.Tls12 | SslProtocols.Tls13 };
        handler.ClientCertificates.Add(_certificate);
        handler.ServerCertificateCustomValidationCallback=(_,server,_,errors)=>errors!=SslPolicyErrors.RemoteCertificateNotAvailable && CertificatePins.Matches(server,options.TrustedServerSha256);
        _http=new(handler){Timeout=TimeSpan.FromSeconds(options.ProbeTimeoutSeconds), MaxResponseContentBufferSize=16384};
    }
    public async Task<ProbeSample> CheckAsync(CancellationToken ct)
    {
        var heartbeat=HeartbeatAsync(ct);var ping=NetworkAsync(ct);var port=PortAsync(ct);
        await Task.WhenAll(heartbeat,ping,port);
        var hb=await heartbeat;var network=await ping;var tcp=await port;
        return new(DateTimeOffset.UtcNow,hb.Heartbeat is { AgentHealthy:true },hb.Latency,network.Reachable || hb.TransportOk,tcp.Port,hb.Heartbeat,
            $"Heartbeat={hb.Detail}; ICMP={(network.Icmp ? "reply" : "no reply (may be filtered)")}; agent TCP={(network.AgentTcp ? "open" : "unavailable")}; Minecraft TCP={(tcp.Port ? "open" : "unavailable")}; process={hb.Heartbeat?.ProcessEvidence ?? "unknown"}; Minecraft status={tcp.Responsive?.ToString() ?? "not checked"}",tcp.Responsive);
    }
    private async Task<(AgentHeartbeat? Heartbeat,double? Latency,bool TransportOk,string Detail)> HeartbeatAsync(CancellationToken ct)
    {
        var sw=Stopwatch.StartNew();
        try
        {
            using var response=await _http.GetAsync($"https://{_options.HostAddress}:{_options.AgentPort}/heartbeat",ct);
            if(!response.IsSuccessStatusCode) return(null,sw.Elapsed.TotalMilliseconds,true,"HTTP "+(int)response.StatusCode);
            // Bound response memory and reject stale/future cached heartbeats.
            var hb=JsonSerializer.Deserialize<AgentHeartbeat>(await response.Content.ReadAsStringAsync(ct),new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if(hb is null || Math.Abs((DateTimeOffset.UtcNow-hb.ObservedAt).TotalSeconds)>30) return(null,sw.Elapsed.TotalMilliseconds,true,"invalid or stale heartbeat / clock skew");
            return(hb,sw.Elapsed.TotalMilliseconds,true,hb.AgentHealthy?"healthy":"agent unhealthy");
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException or JsonException)
        { ct.ThrowIfCancellationRequested();return(null,null,false,"timeout, TLS/authentication or transport failure"); }
    }
    private async Task<(bool Reachable,bool Icmp,bool AgentTcp)> NetworkAsync(CancellationToken ct)
    {
        var ping=PingAsync(ct);
        var agentPort=MinecraftStatusProbe.CheckAsync(_options.HostAddress,_options.AgentPort,false,TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds),ct);
        await Task.WhenAll(ping,agentPort);var icmp=await ping;var tcp=(await agentPort).Port;
        return(icmp || tcp,icmp,tcp);
    }
    private async Task<bool> PingAsync(CancellationToken ct)
    {
        try { using var ping=new Ping();var reply=await ping.SendPingAsync(_options.HostAddress,_options.ProbeTimeoutSeconds*1000).WaitAsync(ct);return reply.Status==IPStatus.Success; }
        catch(Exception ex) when(ex is PingException or SocketException or InvalidOperationException or PlatformNotSupportedException or UnauthorizedAccessException) { return false; }
    }
    private Task<(bool Port,bool? Responsive)> PortAsync(CancellationToken ct) => MinecraftStatusProbe.CheckAsync(_options.HostAddress,_options.MinecraftPort,_options.MinecraftStatusEnabled,TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds),ct);
    public void Dispose() { _http.Dispose();_certificate.Dispose(); }
}
