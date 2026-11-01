using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SSC.Agent;
using SSC.Infrastructure;
using Xunit;
namespace SSC.IntegrationTests;
public sealed class HeartbeatTlsTests
{
    private sealed class Secrets(string password) : ISecretStore { public string Read(string _) => password; }
    private static X509Certificate2 CreateCertificate(string name,string usage)
    {
        using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN="+name,rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(usage) },true));
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        return new(cert.Export(X509ContentType.Pfx));
    }
    [Fact] public async Task RealHttpsEndpointRequiresTrustedClientAndProbeValidatesServerPin()
    {
        using var server=CreateCertificate("AM4","1.3.6.1.5.5.7.3.1");using var client=CreateCertificate("AM5","1.3.6.1.5.5.7.3.2");
        var temporary=Path.Combine(Path.GetTempPath(),"ssc-cert-"+Guid.NewGuid()+".pfx");
        await File.WriteAllBytesAsync(temporary,client.Export(X509ContentType.Pfx,"test-password"));
        using var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();var port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        var options=new HeartbeatOptions { BindAddress="127.0.0.1",Port=port,TrustedClientSha256=client.GetCertHashString(HashAlgorithmName.SHA256) };
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k=>HeartbeatEndpoint.Configure(k,options,server));builder.Services.AddSingleton<WindowsHeartbeatCollector>();
        await using var app=builder.Build();HeartbeatEndpoint.Map(app,options);await app.StartAsync();
        try
        {
            using var anonymousHandler=new HttpClientHandler { ServerCertificateCustomValidationCallback=(_,_,_,_)=>true,UseProxy=false };
            using var anonymous=new HttpClient(anonymousHandler) { Timeout=TimeSpan.FromSeconds(3) };
            await Assert.ThrowsAsync<HttpRequestException>(()=>anonymous.GetAsync($"https://127.0.0.1:{port}/heartbeat"));
            var probeOptions=new WatchdogOptions { HostAddress="127.0.0.1",AgentPort=port,MinecraftPort=port,MinecraftStatusEnabled=false,ClientCertificatePath=temporary,TrustedServerSha256=server.GetCertHashString(HashAlgorithmName.SHA256) };
            using var probe=new NetworkWatchdogProbe(probeOptions,new Secrets("test-password"));
            var sample=await probe.CheckAsync(CancellationToken.None);
            Assert.True(sample.HeartbeatOk);Assert.NotNull(sample.HeartbeatLatencyMs);Assert.True(sample.NetworkReachable);Assert.NotNull(sample.Heartbeat);
            using var wrongPin=new NetworkWatchdogProbe(probeOptions with { TrustedServerSha256=new('0',64) },new Secrets("test-password"));
            Assert.False((await wrongPin.CheckAsync(CancellationToken.None)).HeartbeatOk);
            using var wrongClientCert=CreateCertificate("untrusted","1.3.6.1.5.5.7.3.2");
            using var wrongHandler=new HttpClientHandler { ServerCertificateCustomValidationCallback=(_,_,_,_)=>true,UseProxy=false };
            wrongHandler.ClientCertificates.Add(wrongClientCert);using var wrongClient=new HttpClient(wrongHandler) { Timeout=TimeSpan.FromSeconds(3) };
            await Assert.ThrowsAsync<HttpRequestException>(()=>wrongClient.GetAsync($"https://127.0.0.1:{port}/heartbeat"));
        }
        finally { await app.StopAsync();File.Delete(temporary); }
    }
}
