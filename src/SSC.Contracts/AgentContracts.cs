using SSC.Core;
namespace SSC.Contracts;
// Versioned data only. No transport is enabled in Phase 1.
public static class Protocol { public const int Version = 1; }
public sealed record AgentIdentity(string AgentId, string MachineName, int ProtocolVersion);
public sealed record Heartbeat(AgentIdentity Agent, DateTimeOffset SentAt, IReadOnlyList<Capability> Capabilities);
public sealed record TelemetryEnvelope(int ProtocolVersion, Guid CorrelationId, TelemetrySnapshot Snapshot);
public sealed record ControlEnvelope(int ProtocolVersion, Guid CorrelationId, ServerAction Action, bool OperatorConfirmed);
public sealed record ControlResult(Guid CorrelationId, bool Accepted, string Detail);
public sealed record AgentOptions
{
    public bool Simulation { get; init; } = true;
    public bool RemoteAdministrationEnabled { get; init; } = false;
    public string ServerDirectory { get; init; } = @"C:\Minecraft Server\ATM Aero";
    public int MinecraftPort { get; init; } = 25565;
}
