using Microsoft.Extensions.Hosting;
using Serilog;
namespace SSC.Infrastructure;
public static class OperationalLogging
{
    public static IHostBuilder AddOperationalLogging(this IHostBuilder host,string role) => host.UseSerilog((_,_,log)=>
    {
        log.MinimumLevel.Information().Enrich.FromLogContext()
            .WriteTo.File(new Serilog.Formatting.Json.JsonFormatter(), Path.Combine(ServicePaths.Root,role,"logs",role+"-.jsonl"),
                rollingInterval:RollingInterval.Day,fileSizeLimitBytes:10*1024*1024,rollOnFileSizeLimit:true,retainedFileCountLimit:14,shared:true);
        if(OperatingSystem.IsWindows())log.WriteTo.EventLog("SSC."+role,"Application",manageEventSource:false);
    });
}
