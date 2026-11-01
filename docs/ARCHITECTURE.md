# Architecture and security boundaries

Desktop → Core interfaces → Infrastructure. Contracts reference Core; Agent and Watchdog compose Infrastructure independently. WPF code-behind is restricted to window lifecycle and keyboard event routing. MainViewModel owns presentation state and invokes injected telemetry/store interfaces. Generic Host supplies configuration, DI and Serilog.

Ten modules cover overview, performance/tick health, players, diagnostics, worlds, console, security, lifecycle, alerts/independent availability, and history/incidents. Only local simulation, module navigation/filtering, logging and telemetry persistence execute in Phase 1. Other rows describe planned capabilities.

## Persistence v1

`schema_migrations(version, applied_utc)` tracks an atomic migration. The reviewed v1 SQL is embedded in `SqliteMetricStore.InitializeAsync` so deployment cannot omit a separate SQL asset. Future upgrades must append versioned migrations rather than edit v1.

- `telemetry_snapshots`: UTC timestamp, explicit origin and JSON snapshot including metric availability/reasons and capabilities; indexed by time.
- `incidents`: identity, time, severity, source, summary and origin.
- `player_sessions`: identity, player identifier, join and optional leave times.
- `connection_attempts`: time, address, outcome and source.
- `audit_events`: identity, time, actor, action, outcome and correlation ID.

Only snapshot read/write is implemented; the other tables reserve the domain schema for later adapters. Metrics retain unavailable states and never substitute zero for missing TPS/MSPT.

## Remote administration design (not implemented)

No executable opens a network listener or uses RCON, a webhook, the provided Minecraft TCP port or server paths. Authentication/encryption cannot be claimed implemented while transport is absent.

Before remote control is enabled, build a mutually authenticated TLS transport restricted to a private LAN/VPN. Validate certificates, identities and roles; deny anonymous calls; reject unsupported protocol versions and replayed request IDs. Bind the agent to a specific private interface, apply Windows Firewall scope rules, and audit all requests without secrets. Never publish an administrative endpoint or RCON port to the internet. An AM5 workstation should access AM4 only through that private authenticated channel.

Require an explicit action-specific confirmation for stop/restart and destructive world/player operations. Confirmation is not authentication. A policy layer must validate the action and current state independently. Use typed operation enums and fixed executable/argument policies rather than arbitrary shell or Minecraft command forwarding. Separate desktop shutdown from server shutdown. Recovery needs attempt limits, backoff and incident escalation to avoid crash loops.

Store Discord tokens/RCON credentials with Windows Credential Manager or a DPAPI-backed provider under the service identity. Never put secrets in source, logs, configuration examples or exported diagnostics. Add request redaction and secret-leak tests when the delivery adapter exists.

The independent watchdog must run on AM5 or another independent host: it cannot detect an AM4 host outage when running only on AM4. It should distinguish Minecraft-port failure, agent-heartbeat failure and host reachability using timeouts, multiple failed samples, recovery notifications and alert deduplication. ICMP failure alone is insufficient evidence of host outage.

## Known operational gaps

No live counters, process identity selection, RCON, log tailing, mod inventory, companion detection, CSV import, HTTP clients, Discord delivery, real outage checks, crash recovery or incident export are implemented. Multi-instance migration contention and database retention/encryption policy require production review. Existing security monitor behavior must be audited before integration. The UI stores only simulation fixtures and cannot operate a server.
