using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SSC.Infrastructure;
using SSC.Watchdog;

try
{
    Serilog.Log.Logger=OperationalLogging.CreateBootstrapLogger("Watchdog");
    var builder=Host.CreateDefaultBuilder(args).UseContentRoot(AppContext.BaseDirectory)
        .ConfigureAppConfiguration(c=>c.AddJsonFile(Path.Combine(ServicePaths.Root,"Watchdog","service.json"),optional:true).AddEnvironmentVariables("SSC_"))
        .UseWindowsService(o=>o.ServiceName="SSC.Watchdog").AddOperationalLogging("Watchdog")
        .ConfigureServices((context,services)=>
        {
            var options=context.Configuration.GetSection("Watchdog").Get<WatchdogOptions>() ?? new();
            var discord=context.Configuration.GetSection("Discord").Get<DiscordOptions>() ?? new();
            services.AddSingleton(options);services.AddSingleton(discord);
            services.AddSingleton<ISecretStore,MachineSecretStore>();
            services.AddSingleton(new WatchdogRepository(Path.Combine(ServicePaths.Watchdog,"watchdog.db")));
            services.AddSingleton(new WatchdogStatusFile(ServicePaths.Dashboard));
            services.AddSingleton<SSC.Core.IWatchdogProbe,NetworkWatchdogProbe>();
            services.AddSingleton(new HttpClient(new HttpClientHandler { AllowAutoRedirect=false }){Timeout=TimeSpan.FromSeconds(8)});
            services.AddSingleton<DiscordDelivery>();
            services.AddHostedService<MonitorWorker>();services.AddHostedService<DeliveryWorker>();
        });
    using var host=builder.Build();
    var repository=host.Services.GetRequiredService<WatchdogRepository>();repository.Initialize();
    if(args.Contains("--retry-alerts")) { repository.RetryHeld(DateTimeOffset.UtcNow);Console.WriteLine("Held alerts requeued; start service to resume delivery.");return 0; }
    if(args.Contains("--test-alert"))
    {
        var now=DateTimeOffset.UtcNow;var testId=Guid.NewGuid();repository.Enqueue(new(testId,now,"OPERATOR TEST",
            $"SHADOW SERVER COMMAND — OPERATOR TEST\nThis is a safe Discord delivery test. No Minecraft action or outage is implied.\nCREATED: {now:O}"));
        await host.Services.GetRequiredService<DiscordDelivery>().PumpAsync(now,CancellationToken.None);
        var sent=repository.IsDelivered(testId);
        Console.WriteLine(sent?"OPERATOR TEST delivery confirmed by Discord HTTP success. Verify the message in the designated channel.":"Test alert queued; delivery not confirmed. Check secure provisioning, network access and queue status.");return sent?0:3;
    }
    await host.RunAsync();return Environment.ExitCode;
}
catch(Exception ex)
{
    TlsCertificateLoader.LogFailure(new Serilog.Extensions.Logging.SerilogLoggerFactory(Serilog.Log.Logger, dispose: false).CreateLogger("SSC.Tls"),"Watchdog startup","startup",ex);
    Console.Error.WriteLine("SSC.Watchdog failed. Check configuration, private endpoint, certificates, DPAPI ACLs, Event Log source and writable data directory.");return 1;
}

finally { Serilog.Log.CloseAndFlush(); }
