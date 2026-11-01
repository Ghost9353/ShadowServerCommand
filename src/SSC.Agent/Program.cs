using Microsoft.Extensions.Hosting.WindowsServices;
using SSC.Agent;
using SSC.Infrastructure;

var journal=new AgentStartupJournal();
journal.Write("ProcessEntry");
try
{
    var builder=WebApplication.CreateBuilder(new WebApplicationOptions { Args=args,ContentRootPath=AppContext.BaseDirectory });
    builder.Configuration.AddJsonFile(Path.Combine(ServicePaths.Root,"Agent","service.json"),optional:true).AddEnvironmentVariables("SSC_");
    builder.Host.UseWindowsService(o=>o.ServiceName="SSC.Agent").AddOperationalLogging("Agent");
    builder.Services.AddSingleton(journal);
    if(OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
        builder.Services.AddSingleton<IHostLifetime,AgentWindowsServiceLifetime>();
    var options=builder.Configuration.GetSection("Heartbeat").Get<HeartbeatOptions>() ?? new();
    if(!options.Enabled)throw new InvalidOperationException("Heartbeat disabled; configure the private mTLS endpoint.");
    options.Validate();journal.Write("ConfigurationReady");
    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton<ISecretStore,MachineSecretStore>();
    builder.Services.AddSingleton<AgentCertificateOwner>();
    builder.Services.AddSingleton<WindowsHeartbeatCollector>();
    // Kestrel options are materialized when its hosted service starts, after SCM connects.
    // No DPAPI decryption, machine-key import or signing runs before the dispatcher handshake.
    builder.WebHost.ConfigureKestrel(k=>
    {
        var owner=k.ApplicationServices.GetRequiredService<AgentCertificateOwner>();
        HeartbeatEndpoint.Configure(k,options,owner.GetCertificate(),k.ApplicationServices.GetRequiredService<ILogger<AgentCertificateOwner>>());
    });
    journal.Write("HostBuildBegin");
    await using var app=builder.Build();
    journal.Write("HostBuilt");
    app.Lifetime.ApplicationStarted.Register(()=>journal.Write("HttpsReady"));
    HeartbeatEndpoint.Map(app,options);
    await app.RunAsync();return 0;
}
catch(Exception ex)
{
    journal.Write("StartupFailed",ex);
    TlsCertificateLoader.LogFailure(new Serilog.Extensions.Logging.SerilogLoggerFactory(Serilog.Log.Logger,dispose:false).CreateLogger("SSC.Tls"),"Agent startup","startup",ex);
    Console.Error.WriteLine("SSC.Agent startup failed. Inspect Agent logs/startup-*.jsonl for the exact stage and HRESULT.");
    return 1;
}
finally { Serilog.Log.CloseAndFlush(); }
