using SSC.Core;
namespace SSC.Infrastructure;
public sealed class SimulatedTelemetrySource : ITelemetrySource, ICapabilityDetector
{
    public Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new TelemetrySnapshot(DateTimeOffset.UtcNow, DataOrigin.Simulated, "SIMULATED ONLINE", [
            new("CPU",31,"%",MetricState.Available), new("Memory",18.6,"GiB",MetricState.Available),
            new("Java heap",8.4,"GiB",MetricState.Available), new("Players",3,"players",MetricState.Available),
            Metric.Unavailable("TPS","ticks/s","Spark or companion not configured"),
            Metric.Unavailable("MSPT","ms","Spark or companion not configured")], Capabilities));
    }
    private static readonly Capability[] Capabilities = [new("Simulation",true,"Built-in fixture; never accesses live server"),new("RCON",false,"Not probed in Phase 1"),new("Spark",false,"Not probed in Phase 1"),new("Companion",false,"Not probed in Phase 1")];
    public Task<IReadOnlyList<Capability>> DetectAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Capability>>(Capabilities);
}
