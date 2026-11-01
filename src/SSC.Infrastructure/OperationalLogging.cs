using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
namespace SSC.Infrastructure;
public static class OperationalLogging
{
    public static Serilog.ILogger CreateBootstrapLogger(string role) => new LoggerConfiguration().MinimumLevel.Information()
        .WriteTo.File(new Serilog.Formatting.Json.JsonFormatter(), Path.Combine(ServicePaths.Root,role,"logs",role+"-.jsonl"),
            rollingInterval:RollingInterval.Day,fileSizeLimitBytes:10*1024*1024,rollOnFileSizeLimit:true,retainedFileCountLimit:14,shared:true).CreateLogger();
    public static IHostBuilder AddOperationalLogging(this IHostBuilder host,string role) => host.UseSerilog((_,_,log)=>
    {
        log.MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore.Server.Kestrel.Https", LogEventLevel.Debug).Enrich.FromLogContext()
            .WriteTo.File(new Serilog.Formatting.Json.JsonFormatter(), Path.Combine(ServicePaths.Root,role,"logs",role+"-.jsonl"),
                rollingInterval:RollingInterval.Day,fileSizeLimitBytes:10*1024*1024,rollOnFileSizeLimit:true,retainedFileCountLimit:14,shared:true);
        if(OperatingSystem.IsWindows())log.WriteTo.EventLog("SSC."+role,"Application",manageEventSource:false);
    });
}
