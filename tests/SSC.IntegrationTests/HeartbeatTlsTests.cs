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
        return TlsCertificateLoaderTests.Create(name,usage);

    }
    [Fact] public async Task RealHttpsEndpointRequiresTrustedClientAndProbeValidatesServerPin()
    {
        using var serverFixture=CreateCertificate("AM4","1.3.6.1.5.5.7.3.1");using var client=CreateCertificate("AM5","1.3.6.1.5.5.7.3.2");
        var serverFile=Path.Combine(Path.GetTempPath(),"ssc-server-"+Guid.NewGuid()+".pfx");
        await File.WriteAllBytesAsync(serverFile,serverFixture.Export(X509ContentType.Pfx,"server-password"));
        using var server=TlsCertificateLoader.Load(serverFile,new Secrets("server-password"),"agent-pfx",TlsCertificatePurpose.Server);
        var temporary=Path.Combine(Path.GetTempPath(),"ssc-cert-"+Guid.NewGuid()+".pfx");
        await File.WriteAllBytesAsync(temporary,client.Export(X509ContentType.Pfx,"test-password"));
        using var reservation=new TcpListener(IPAddress.Loopback,0);reservation.Start();var port=((IPEndPoint)reservation.LocalEndpoint).Port;reservation.Stop();
        var options=new HeartbeatOptions { BindAddress="127.0.0.1",Port=port,TrustedClientSha256=client.GetCertHashString(HashAlgorithmName.SHA256) };
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k=>HeartbeatEndpoint.Configure(k,options,server));builder.Services.AddSingleton<WindowsHeartbeatCollector>();
        await using var app=builder.Build();HeartbeatEndpoint.Map(app,options);await app.StartAsync();
        try
        {
            using var anonymousHandler=new HttpClientHandler { ServerCertificateCustomValidationCallback=(_,peer,chain,errors)=>CertificatePins.ValidatePeer(peer,server.GetCertHashString(HashAlgorithmName.SHA256),TlsCertificatePurpose.Server,null,chain,errors),UseProxy=false };
            using var anonymous=new HttpClient(anonymousHandler) { Timeout=TimeSpan.FromSeconds(3) };
            await Assert.ThrowsAsync<HttpRequestException>(()=>anonymous.GetAsync($"https://127.0.0.1:{port}/heartbeat"));
            var probeOptions=new WatchdogOptions { HostAddress="127.0.0.1",AgentPort=port,MinecraftPort=port,MinecraftStatusEnabled=false,ClientCertificatePath=temporary,TrustedServerSha256=server.GetCertHashString(HashAlgorithmName.SHA256) };
            var logger=new TlsCertificateLoaderTests.RecordingLogger<NetworkWatchdogProbe>();
            using var probe=new NetworkWatchdogProbe(probeOptions,new Secrets("test-password"),logger);
            var sample=await probe.CheckAsync(CancellationToken.None);
            Assert.True(sample.HeartbeatOk);Assert.NotNull(sample.HeartbeatLatencyMs);Assert.True(sample.NetworkReachable);Assert.NotNull(sample.Heartbeat);
            using var wrongPin=new NetworkWatchdogProbe(probeOptions with { TrustedServerSha256=new('0',64) },new Secrets("test-password"),logger);
            var failed=await wrongPin.CheckAsync(CancellationToken.None);
            Assert.False(failed.HeartbeatOk);Assert.True(failed.NetworkReachable);Assert.True(failed.MinecraftPortOpen);
            Assert.Contains(logger.Messages,m=>m.Contains("TLS peer rejected"));
            Assert.Contains(logger.Messages,m=>m.Contains("Heartbeat request failed"));
            var state=new SSC.Core.WatchdogState();var detector=new SSC.Core.WatchdogDetector(new());
            var tick=DateTimeOffset.UtcNow;
            for(var i=0;i<3;i++)detector.Apply(state,failed with { ObservedAt=tick.AddSeconds(15*i) });
            Assert.Equal(SSC.Core.ComputerCondition.AgentUnavailable,state.Computer);Assert.Null(state.LastHeartbeat);
            var recovered=await probe.CheckAsync(CancellationToken.None);Assert.True(recovered.HeartbeatOk);
            IReadOnlyList<SSC.Core.PendingAlert> alerts=[];
            for(var i=3;i<6;i++)alerts=detector.Apply(state,recovered with { ObservedAt=tick.AddSeconds(15*i) });
            Assert.Equal(SSC.Core.ComputerCondition.Online,state.Computer);Assert.NotNull(state.LastHeartbeat);
            Assert.Contains(alerts,a=>a.Kind.Contains("RECOVERED"));
            using var wrongClientFixture=CreateCertificate("untrusted","1.3.6.1.5.5.7.3.2");
            var wrongFile=Path.Combine(Path.GetTempPath(),"ssc-wrong-"+Guid.NewGuid()+".pfx");
            await File.WriteAllBytesAsync(wrongFile,wrongClientFixture.Export(X509ContentType.Pfx,"pass"));
            using var wrongClientCert=TlsCertificateLoader.Load(wrongFile,new Secrets("pass"),"watchdog-pfx",TlsCertificatePurpose.Client);
            File.Delete(wrongFile);
            using var wrongHandler=new HttpClientHandler { ServerCertificateCustomValidationCallback=(_,peer,chain,errors)=>CertificatePins.ValidatePeer(peer,server.GetCertHashString(HashAlgorithmName.SHA256),TlsCertificatePurpose.Server,null,chain,errors),UseProxy=false };
            wrongHandler.ClientCertificates.Add(wrongClientCert);using var wrongClient=new HttpClient(wrongHandler) { Timeout=TimeSpan.FromSeconds(3) };
            await Assert.ThrowsAsync<HttpRequestException>(()=>wrongClient.GetAsync($"https://127.0.0.1:{port}/heartbeat"));
        }
        finally { await app.StopAsync();File.Delete(temporary);File.Delete(serverFile); }
    }
}
