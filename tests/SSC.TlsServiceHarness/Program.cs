using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting.WindowsServices;
using SSC.Agent;
using SSC.Infrastructure;
if(!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(ServicePaths.Root,"fixture-only.marker")))return 2;
var journal=new AgentStartupJournal();journal.Write("ProcessEntry");
X509Certificate2? certificate=null;
try
{
    var builder=WebApplication.CreateBuilder(args);
    builder.Configuration.AddJsonFile(Path.Combine(ServicePaths.Root,"Agent","service.json"));
    builder.Host.UseWindowsService(o=>o.ServiceName="SSC.Agent");
    builder.Services.AddSingleton(journal);builder.Services.AddSingleton<IHostLifetime,AgentWindowsServiceLifetime>();
    var options=builder.Configuration.GetSection("Heartbeat").Get<HeartbeatOptions>()!;
    var mode=builder.Configuration["DiagnosticMode"]??"Machine";
    builder.Services.AddSingleton<WindowsHeartbeatCollector>();
    builder.Services.AddSingleton<IHostedService>(new DiagnosticInitializer(()=>
    {
        journal.Write("Diagnostic"+mode);
        var secrets=new MachineSecretStore();
        if(mode=="Machine")certificate=TlsCertificateLoader.Load(options.CertificatePath,secrets,options.CertificatePasswordSecret,TlsCertificatePurpose.Server,null,s=>journal.Write(s));
        else if(mode=="Ephemeral")
        {
            certificate=new X509Certificate2(options.CertificatePath,secrets.Read(options.CertificatePasswordSecret),X509KeyStorageFlags.EphemeralKeySet);
            journal.Write("EphemeralImportComplete");TlsCertificateLoader.ValidateSigningCertificate(certificate,TlsCertificatePurpose.Server,null,s=>journal.Write(s));
        }
        else throw new InvalidOperationException("Unsupported diagnostic mode.");
    }));
    builder.WebHost.ConfigureKestrel(k=>HeartbeatEndpoint.Configure(k,options,null,null,()=>certificate!));
    await using var app=builder.Build();app.Lifetime.ApplicationStarted.Register(()=>journal.Write("HttpsReady"));HeartbeatEndpoint.Map(app,options);
    await app.RunAsync();return 0;
}
catch(Exception ex){journal.Write("StartupFailed",ex);return 1;}
finally{certificate?.Dispose();}
internal sealed class DiagnosticInitializer(Action initialize) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)=>await Task.Run(initialize,ct).WaitAsync(TimeSpan.FromSeconds(20),ct);
    public Task StopAsync(CancellationToken ct)=>Task.CompletedTask;
}
