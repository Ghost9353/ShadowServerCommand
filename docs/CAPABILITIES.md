# Capability detection and future setup runbook

These procedures describe later operator work. Do not apply them during Phase 1. No integration is assumed configured. The Phase 1 capability provider reports RCON, Spark and a companion unavailable/not probed.

## Establish a read-only baseline

On AM4, record the process executable, command line, Java 21 vendor/build, Minecraft 1.21.1 and NeoForge version from trusted server startup logs. Confirm the installation is `C:\Minecraft Server\ATM Aero` and game TCP port is 25565. Do not infer process identity merely from `java.exe` or select a random Java process. Capture existing service/scheduled-task behavior and backup procedures before adding control.

Audit `C:\Minecraft Server\ATM Aero\MinecraftSecurityMonitor` without modifying it. Document ownership, startup task/service, watched ports, event sources, deduplication, retention, alert behavior and restart semantics. Inspect a **copy** of `C:\Minecraft Server\ATM Aero\SecurityMonitor\connection-attempts.csv` to define encoding, headers, timestamps, timezone, IP fields and malformed-row handling. Import must be read-only, idempotent and preserve source provenance. Never replace the existing monitor until migration is reviewed and verified.

## Windows and Java metrics

Use explicit read-only process identity configuration and least-privilege Windows process/counter access. Distinguish working set, committed bytes and Java heap; working set is not heap usage. Java heap requires an authenticated local JMX strategy or a vetted companion. Do not enable unauthenticated remote JMX. Missing permissions or unavailable counters must return a reason and timestamp rather than zero.

## RCON (optional control capability)

1. Review the server's backup and downtime plan; changing `server.properties` needs a separately approved maintenance window.
2. Generate a unique strong password and store it outside source control in an OS secret store. If enabling RCON, configure its password and a dedicated port per the server's documentation.
3. Restrict the RCON port with Windows Firewall to local agent access only; confirm it is unreachable from public networks. Do not port-forward it. RCON itself does not provide encrypted transport.
4. Verify authentication locally with a harmless query. Detect capability only after a successful authenticated exchange, with a short timeout and no secret logging.
5. Desktop access must traverse the future private mTLS agent channel; the desktop must not connect directly over public RCON. Disable capability on auth failure.

RCON does not guarantee TPS/MSPT support. Never parse invented tick values from an unrelated response.

## Spark or companion tick telemetry

Check the installed mod list and version-specific documentation for a **Minecraft 1.21.1 NeoForge-compatible** Spark build. Do not download or insert a mod automatically. Back up and use a maintenance window for an approved install. Validate supported commands/API for the actual version and permissions. A successful capability handshake must identify source/version, sample time, TPS/MSPT units and supported windows. Do not hard-code a log parser based on assumed output. A companion mod is an alternative only after its API, authentication and compatibility are defined and tested. Until then TPS, MSPT, chunk counts, dimension ticks and heap remain unavailable.

## Discord and independent watchdog

Create an operator-approved webhook in the intended Discord channel. Provision its token into the future secret provider, never a checked-in appsettings file. Delivery requires HTTPS, bounded payloads, timeout, 429 Retry-After handling, bounded retries, deduplication and redaction. Test using an explicitly labeled test alert after delivery is implemented.

Deploy Watchdog separately on AM5 or another independent host, choose the AM4 private address, and configure future heartbeat/game-port probes over the private network. Test loss of Minecraft separately from loss of the AM4 host, followed by recovery. Phase 1 Watchdog logs scaffold heartbeats only and must not be relied on for outage detection.
