using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using SSC.Infrastructure;

try
{
    var builder=WebApplication.CreateBuilder(new WebApplicationOptions { Args=args,ContentRootPath=AppContext.BaseDirectory });
    builder.Configuration.AddJsonFile(Path.Combine(ServicePaths.Root,"Agent","service.json"),optional:true).AddEnvironmentVariables("SSC_");
    builder.Host.UseWindowsService(o=>o.ServiceName="SSC.Agent").AddOperationalLogging("Agent");
    var options=builder.Configuration.GetSection("Heartbeat").Get<HeartbeatOptions>() ?? new();
    if(!options.Enabled) { Console.Error.WriteLine("Heartbeat disabled. Configure the private mTLS endpoint before starting SSC.Agent.");return 2; }
    options.Validate();
    var secrets=new MachineSecretStore();
    using var certificate=new X509Certificate2(options.CertificatePath,secrets.Read(options.CertificatePasswordSecret),X509KeyStorageFlags.EphemeralKeySet);
    if(!certificate.HasPrivateKey)throw new InvalidOperationException("Agent certificate needs a private key.");
    builder.WebHost.ConfigureKestrel(k=>SSC.Agent.HeartbeatEndpoint.Configure(k,options,certificate));
    builder.Services.AddSingleton<WindowsHeartbeatCollector>();
    await using var app=builder.Build();
    SSC.Agent.HeartbeatEndpoint.Map(app,options);
    await app.RunAsync();return 0;
}
catch(Exception)
{
    // Avoid printing certificate-password or configuration details on startup failure.
    Console.Error.WriteLine("SSC.Agent startup failed. Check private bind address, certificate pins, secret ACLs, Event Log source and service configuration.");
    return 1;
}
