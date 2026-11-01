namespace SSC.Core;

public enum ComputerCondition { Unknown, Online, AgentUnavailable, Unreachable }
public enum MinecraftCondition { Unknown, Running, ProcessStopped, PortUnavailable, PotentiallyUnresponsive }
public sealed record AgentHeartbeat(DateTimeOffset ObservedAt, double UptimeSeconds, bool AgentHealthy,
    bool? MinecraftRunning, string ProcessEvidence, double? CpuPercent, long? AvailableMemoryBytes,
    long? MinecraftWorkingSetBytes, string AgentVersion, long? TotalMemoryBytes = null, double? MemoryUsedPercent = null);
public sealed record ProbeSample(DateTimeOffset ObservedAt, bool HeartbeatOk, double? HeartbeatLatencyMs,
    bool NetworkReachable, bool MinecraftPortOpen, AgentHeartbeat? Heartbeat, string Evidence, bool? MinecraftResponsive = null);
public sealed record WatchdogIncident(Guid Id, string Kind, DateTimeOffset StartedAt, DateTimeOffset DetectedAt,
    DateTimeOffset? RecoveredAt, string Evidence, bool ObservationInterrupted = false);
public sealed record MonitoringGap(DateTimeOffset From, DateTimeOffset Until, int MissedIntervals, string Reason);
public sealed class WatchdogState
{
    public ComputerCondition Computer { get; set; }
    public MinecraftCondition Minecraft { get; set; }
    public DateTimeOffset? LastCheck { get; set; }
    public DateTimeOffset? LastHeartbeat { get; set; }
    public AgentHeartbeat? LastAgentHeartbeat { get; set; }
    public double? HeartbeatLatencyMs { get; set; }
    public int Failures { get; set; }
    public int Successes { get; set; }
    public DateTimeOffset? ComputerFailureSince { get; set; }
    public DateTimeOffset? MinecraftFailureSince { get; set; }
    public bool EscalationSent { get; set; }
    public MinecraftCondition Candidate { get; set; }
    public int MinecraftFailures { get; set; }
    public int MinecraftSuccesses { get; set; }
    public Guid? ComputerIncident { get; set; }
    public Guid? MinecraftIncident { get; set; }
    public List<WatchdogIncident> Incidents { get; set; } = [];
    public List<MonitoringGap> Gaps { get; set; } = [];
    public List<DateTimeOffset> CrashTimes { get; set; } = [];
    public DateTimeOffset? LastCrashBurstAlert { get; set; }
    public bool? LastProcessRunning { get; set; }
    public string LastEvidence { get; set; } = "No checks yet";
}
public sealed record PendingAlert(Guid Id, DateTimeOffset CreatedAt, string Kind, string Message);
public sealed record WatchdogDashboard(DateTimeOffset WrittenAt, string ServiceHealth, WatchdogState State,
    int PendingAlerts, int HeldAlerts, DateTimeOffset? LastDelivered, string DeliveryStatus);
public interface IWatchdogStatusSource { Task<WatchdogDashboard?> ReadAsync(CancellationToken ct = default); }
public interface IWatchdogProbe { Task<ProbeSample> CheckAsync(CancellationToken ct); }
public sealed record DetectionOptions
{
    public int IntervalSeconds { get; init; } = 15;
    public int FailureThreshold { get; init; } = 3;
    public int RecoveryThreshold { get; init; } = 3;
    public int CrashWindowMinutes { get; init; } = 15;
    public int CrashBurstCount { get; init; } = 3;
    public void Validate()
    {
        if (IntervalSeconds is < 1 or > 300 || FailureThreshold is < 1 or > 100 || RecoveryThreshold is < 1 or > 100 || CrashWindowMinutes < 1 || CrashBurstCount < 2)
            throw new InvalidOperationException("Invalid watchdog detection settings.");
    }
}
