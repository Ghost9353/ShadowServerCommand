using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SSC.Contracts;
using SSC.Core;
namespace SSC.Infrastructure;
public static class Composition
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShadowServerCommand");
    public static IHostBuilder AddSsc(this IHostBuilder builder, string role) => builder
        .UseSerilog((_, _, log) => log.MinimumLevel.Information().Enrich.FromLogContext()
            .WriteTo.File(path: Path.Combine(DataDirectory, "logs", role + "-.jsonl"),
                formatter: new Serilog.Formatting.Json.JsonFormatter(), rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 14, shared: true))
        .ConfigureServices((context, services) =>
        {
            var options = context.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new();
            if (!options.Simulation || options.RemoteAdministrationEnabled)
                throw new InvalidOperationException("Phase 1 requires Simulation=true and RemoteAdministrationEnabled=false.");
            services.AddSingleton(options);
            services.AddSingleton<ITelemetrySource, SimulatedTelemetrySource>();
            services.AddSingleton<IServerController, DisabledServerController>();
            services.AddSingleton<IMetricStore>(new SqliteMetricStore(Path.Combine(DataDirectory, role + ".db")));
        });
}
