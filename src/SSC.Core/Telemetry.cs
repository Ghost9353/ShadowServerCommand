namespace SSC.Core;

public enum DataOrigin { Simulated, Observed }
public enum MetricState { Available, Unavailable }
public enum Severity { Information, Warning, Critical }
public sealed record Metric(string Name, double? Value, string Unit, MetricState State, string? Reason = null)
{
    public static Metric Unavailable(string name, string unit, string reason) => new(name, null, unit, MetricState.Unavailable, reason);
    public string Display => State == MetricState.Available && Value.HasValue ? $"{Value:0.##} {Unit}" : $"UNAVAILABLE — {Reason}";
}
public sealed record Capability(string Name, bool Available, string Detail);
public sealed record TelemetrySnapshot(DateTimeOffset Timestamp, DataOrigin Origin, string Status, IReadOnlyList<Metric> Metrics, IReadOnlyList<Capability> Capabilities);
public sealed record Incident(Guid Id, DateTimeOffset Timestamp, Severity Severity, string Source, string Summary, DataOrigin Origin);
public sealed record PlayerSession(Guid Id, string PlayerId, DateTimeOffset JoinedAt, DateTimeOffset? LeftAt);
public sealed record ConnectionAttempt(DateTimeOffset Timestamp, string Address, string Outcome, string Source);
public sealed record ControlRequest(Guid Id, ServerAction Action, bool Confirmed);
public enum ServerAction { Start, Stop, Restart }
public interface ITelemetrySource { Task<TelemetrySnapshot> ReadAsync(CancellationToken cancellationToken); }
public interface IMetricStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task AppendAsync(TelemetrySnapshot snapshot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TelemetrySnapshot>> ReadRecentAsync(int limit, CancellationToken cancellationToken = default);
}
public interface IServerController { Task RequestAsync(ControlRequest request, CancellationToken cancellationToken); }
public interface ICapabilityDetector { Task<IReadOnlyList<Capability>> DetectAsync(CancellationToken cancellationToken); }
public interface IAlertSink { Task SendAsync(Incident incident, CancellationToken cancellationToken); }
public interface IAvailabilityProbe { Task<bool> IsAvailableAsync(CancellationToken cancellationToken); }
public interface IServerLogSource { IAsyncEnumerable<string> ReadAsync(CancellationToken cancellationToken); }
public interface IConnectionHistorySource { Task<IReadOnlyList<ConnectionAttempt>> ReadAsync(CancellationToken cancellationToken); }
public interface IPlayerSessionStore { Task<IReadOnlyList<PlayerSession>> ReadAsync(CancellationToken cancellationToken); }
public sealed class DisabledServerController : IServerController
{
    public Task RequestAsync(ControlRequest request, CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException("Live control is disabled in Phase 1. No server action was taken."));
}
