using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SSC.Core;
using SSC.Infrastructure;
namespace SSC.Watchdog;
public sealed class MonitorWorker(WatchdogOptions options,IWatchdogProbe probe,WatchdogRepository repository,
    WatchdogStatusFile status,ILogger<MonitorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if(!options.Enabled)throw new InvalidOperationException("Watchdog disabled; provision service.json first.");
        options.Validate();var state=repository.Load();var detector=new WatchdogDetector(options.Detection);var restarted=true;
        using var timer=new PeriodicTimer(TimeSpan.FromSeconds(options.Detection.IntervalSeconds));
        try
        {
            do
            {
                // Immediate first observation reconciles durable state after boot/restart.
                var sample=await probe.CheckAsync(stoppingToken);
                var alerts=detector.Apply(state,sample,restarted);restarted=false;
                repository.Commit(state,sample,alerts);
                // Archive retains everything; bound the working status/state payload.
                state.Incidents=state.Incidents.Where(i=>i.RecoveredAt is null).Concat(state.Incidents.Where(i=>i.RecoveredAt.HasValue).TakeLast(100)).DistinctBy(i=>i.Id).ToList();
                state.Gaps=state.Gaps.TakeLast(100).ToList();
                var queue=repository.DeliverySummary();
                await status.WriteAsync(new(DateTimeOffset.UtcNow,"RUNNING",state,queue.Pending,queue.Held,queue.LastDelivered,queue.Status),stoppingToken);
                foreach(var alert in alerts)logger.LogWarning("Watchdog event {Kind} ({Id}) queued",alert.Kind,alert.Id);
                logger.LogInformation("Probe completed: heartbeat={Heartbeat}, network={Network}, port={Port}, failures={Failures}",sample.HeartbeatOk,sample.NetworkReachable,sample.MinecraftPortOpen,state.Failures);
            } while(await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
        catch(Exception ex)
        {
            logger.LogCritical("Watchdog monitoring failed ({ErrorType}); exiting for SCM recovery",ex.GetType().Name);
            Serilog.Log.CloseAndFlush();Environment.Exit(1);throw;
        }
        finally
        {
            var queue=repository.DeliverySummary();
            await status.WriteAsync(new(DateTimeOffset.UtcNow,"STOPPED",state,queue.Pending,queue.Held,queue.LastDelivered,queue.Status),CancellationToken.None);
        }
    }
}
public sealed class DeliveryWorker(DiscordDelivery delivery,ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer=new PeriodicTimer(TimeSpan.FromSeconds(2));
            do { await delivery.PumpAsync(DateTimeOffset.UtcNow,stoppingToken); } while(await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) { }
        catch(Exception ex) { logger.LogCritical("Alert delivery worker failed ({ErrorType}); exiting for SCM recovery",ex.GetType().Name);Serilog.Log.CloseAndFlush();Environment.Exit(1);throw; }
    }
}
