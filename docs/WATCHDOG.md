# Independent AM5 watchdog / AM4 heartbeat agent

SSC.Agent runs on **AM4**. SSC.Watchdog runs on **AM5**, under the Windows Service Control Manager, independent of Minecraft, the desktop and interactive login. Leave AM5 powered and awake for continuous monitoring. AM5 cannot send alerts while it is asleep, powered off, or disconnected from Discord; the next startup records the gap and reconciles state.

This feature adds read-only monitoring, not server control. No Minecraft files or existing security-monitor files are modified. Do not install both components only on AM4: that defeats outage detection. The prior Phase 1 documentation describes the original simulation baseline; this guide describes the implemented watchdog extension.

## Checks and interpretation

Default cadence is **15 seconds**, with an immediate check on startup. Independent network probes run concurrently with bounded **4-second** deadlines:

1. HTTPS agent heartbeat with mandatory client certificate and SHA256 server pin.
2. ICMP and a separate TCP connection to the agent port. ICMP failure alone is not evidence of a dead host.
3. Minecraft TCP 25565 connection.
4. Optional Minecraft Java status handshake (enabled by default; protocol 767 for 1.21/1.21.1).
5. Exact Minecraft process identity reported by the agent.

After **three consecutive failed authenticated/healthy heartbeat checks** (usually 30–45 seconds plus probe time from the actual failure), open one host/agent incident. If other probes respond, classify AM4 as reachable / agent unavailable. If no probes respond, classify AM4 as unreachable, with cause unknown. An agent incident can escalate once to host-unreachable; network flapping does not repeatedly send escalation alerts. Do not interpret unreachable as confirmed power loss.

A healthy heartbeat does not depend on Minecraft. Three stopped-process observations open a Minecraft process-stopped incident. A running process with a failing port or status handshake opens a **potentially unresponsive** incident. With unknown process identity and a failed port, report **port unavailable**. A missing heartbeat does not mean Java stopped. Confirm computer recovery only after three healthy heartbeats; confirm Minecraft recovery after three observations of running process, open port and no failed enabled status probe. Recovery alerts include original contact time, detection/recovery times and elapsed incident duration.

Status-handshake success proves the Minecraft status handler answered, not that game ticks are healthy. If `enable-status` is disabled or a mod/proxy suppresses status replies, the watchdog may report potentially unresponsive; set `MinecraftStatusEnabled=false` in Watchdog configuration only after documenting that limitation. This mode cannot distinguish an accepting TCP socket from a stalled Java application. TPS/MSPT still require a supported Spark/companion integration.

Repeated-stop alerts count observed running→stopped transitions within `CrashWindowMinutes` (default 15); `CrashBurstCount` defaults to 3. Intentional stop vs crash is unknown without an approved lifecycle integration. Sustained stopped samples do not count as repeated crashes. Restart/sleep gaps reset consecutive counts and prior process-transition evidence.

## Build packages

Use .NET SDK 8 / Visual Studio 2022 17.8+ from the repository root:

```powershell
dotnet build ShadowServerCommand.sln -c Release
./scripts/verify.ps1
dotnet publish src/SSC.Agent -c Release -r win-x64 --self-contained false -o artifacts/agent
dotnet publish src/SSC.Watchdog -c Release -r win-x64 --self-contained false -o artifacts/watchdog
dotnet publish src/SSC.Desktop -c Release -r win-x64 --self-contained false -o artifacts/desktop
```

Framework-dependent prerequisites: **ASP.NET Core Runtime 8 x64** on AM4, **.NET Runtime 8 x64** on AM5, and **.NET Windows Desktop Runtime 8 x64** for the desktop. Installing an SDK on each PC is unnecessary. Use a currently patched runtime. Scripts require elevated Windows PowerShell 5.1+ on 64-bit Windows; script syntax and actual installation must be validated on Windows.

## Provision certificates and configuration

1. Choose fixed/reserved private IPv4 addresses for AM4 and AM5. Examples below use AM4 `192.168.1.40` and AM5 `192.168.1.50`; replace them. The host network must be the trusted Windows **Private** profile. Keep the firewall enabled. Do not use public IPs, wildcard binding, router port forwarding, UPnP forwarding, public tunnels or a public reverse proxy.
2. On a trusted admin PC, run:

   ```powershell
   ./scripts/New-SscCertificates.ps1 -OutputDirectory C:\SSC-Cert-Staging
   ```

   Choose separate strong passwords at the hidden prompts. The script creates one-year RSA certificates with the appropriate server/client EKUs, exports password-encrypted PFXs to an administrators-only directory and prints **public SHA256 pins**, not private keys. Transfer only `Agent.pfx` to AM4 and only `Watchdog.pfx` to AM5 through a trusted channel. Share the two public pin text files with both PCs. Protect staging/private-key backups. Avoid chat/email/public shares for PFXs or passwords.
3. Copy `config/agent.example.json` to a local working file on AM4. Set its actual private bind address, paste **Watchdog.sha256.txt** into `TrustedClientSha256`, and configure Minecraft process identity as described below. `CertificatePath` is filled by the installer. This heartbeat port is **27443**, distinct from game port 25565; no RCON is used.
4. Copy `config/watchdog.example.json` to AM5. Set AM4's address and paste **Agent.sha256.txt** into `TrustedServerSha256`. Keep defaults 15/3/3 unless deliberately reviewing the detection policy. The installer fills `ClientCertificatePath`. No webhook URL belongs in these files.

Pins are the trust anchors for these self-signed certificates; there is no insecure TLS fallback. Pinning verifies validity dates and exact leaf identity. Reissue and transfer replacement PFXs/pins before expiry; stop/reconfigure/restart both services in a planned window. Pins are not certificate thumbprints from the Windows SHA1 Thumbprint display. Client-certificate EKU is required by the TLS stack. Hostnames are not used as trust anchors in this pin-based private-IP deployment.

### Reliable Java process identity

Prefer automatic rediscovery across Java restarts: set **both** `MinecraftExecutablePath` to the actual Java 21 executable and `MinecraftCommandLineContains` to a unique, verified argument marker for this Minecraft instance. The example NeoForge argument is a template, not detected fact. Review your actual command line locally with `Get-CimInstance Win32_Process -Filter "Name='java.exe'"` and select a marker that cannot match another server. No process command line is logged or sent to Discord.

The agent performs a fixed, read-only WMI query and requires exactly one matching executable + marker. Zero matches mean stopped only when query access is sufficient. Multiple matches, missing permissions and process races mean **unknown**, not stopped. Start from unknown until configured. Check service-identity WMI permissions rather than running as Administrator/LocalSystem to bypass an access error. Windows performance counters may require membership in **Performance Monitor Users**; grant only this group if counters are unavailable. CPU and physical memory usage are measured; unavailable counters are null. Java working set is distinct from Java heap.

An optional `MinecraftProcessId` must be paired with `MinecraftExecutablePath` and `MinecraftProcessStartedAt` (exact UTC creation time). This guards PID reuse but must be updated whenever Java restarts. Automatic marker-based discovery is recommended for crash recovery/reboots. No processes are started, stopped, suspended or killed by the agent.

## Install on AM4 (administrator PowerShell)

```powershell
./scripts/Install-SscService.ps1 -Role Agent `
  -PublishDirectory C:\SSC-Packages\agent `
  -ConfigurationFile C:\SSC-Setup\agent.json `
  -CertificateFile C:\SSC-Setup\Agent.pfx `
  -TrustedWatchdogAddress 192.168.1.50
./scripts/Set-SscSecret.ps1 -Role Agent -Name agent-pfx
Start-Service SSC.Agent
Get-Service SSC.Agent
```

Enter the **Agent PFX password** at the hidden secret prompt. The installer creates an inbound firewall rule scoped to the explicit AM4 address, the single AM5 address, the exact agent binary, port 27443, and Private profile only. It does not alter Minecraft's firewall rules or security monitor. Review existing broader rules separately; adding a narrow allow rule does not revoke an older broad rule. The endpoint still requires the pinned AM5 client certificate.

## Install on AM5

```powershell
./scripts/Install-SscService.ps1 -Role Watchdog `
  -PublishDirectory C:\SSC-Packages\watchdog `
  -ConfigurationFile C:\SSC-Setup\watchdog.json `
  -CertificateFile C:\SSC-Setup\Watchdog.pfx
./scripts/Set-SscSecret.ps1 -Role Watchdog -Name watchdog-pfx
./scripts/Set-SscSecret.ps1 -Role Watchdog -Name discord-webhook
Start-Service SSC.Watchdog
./scripts/Get-SscStatus.ps1
```

At the hidden prompts, supply the **Watchdog PFX password**, then the designated Discord channel's **HTTPS discord.com webhook URL**. The only allowed delivery host is `discord.com:443` with `/api/webhooks/<id>/<token>`; redirects, HTTP, query parameters, userinfo and other destinations are rejected. Do not put a webhook URL in a command argument, transcript, environment variable, repository or log. Use Windows Firewall defaults allowing outbound HTTPS and private probes, or explicitly review narrower outbound rules if your policy blocks them.

Both services use **virtual service accounts** (`NT SERVICE\SSC.Agent` / `NT SERVICE\SSC.Watchdog`), have Automatic startup and SCM restart-after-failure actions (10s, 30s, 60s). They do not require a login. They are installed but deliberately not started until secure provisioning is complete. There is no Desktop/Minecraft service dependency.

## Paths and operations

- Binaries: `%ProgramFiles%\ShadowServerCommand\Agent` or `Watchdog`, service-readable, administrators-writable.
- Local configuration: `%ProgramData%\ShadowServerCommand\<Role>\service.json`. Bundled appsettings defaults are disabled; the local file is loaded afterward. `SSC_`-prefixed standard environment overrides exist for non-secret deployment options, for example `SSC_Watchdog__HostAddress`.
- Passwords/webhook: `%ProgramData%\ShadowServerCommand\Secrets\*.bin`, Windows **DPAPI LocalMachine** protected with restrictive ACLs for Administrators, SYSTEM and the corresponding service identity. Machine-scope protection depends on those ACLs; it does not protect against local administrators. Never grant Users read access to Secrets or move DPAPI blobs to another PC.
- State/history/outbox: `%ProgramData%\ShadowServerCommand\Watchdog\watchdog.db` (SQLite WAL, 10-second busy timeout), separate from Phase 1 simulation databases. State, incidents, raw probe evidence, gaps and pending alerts commit atomically. Each alert ID is enqueued once. History is not automatically deleted. Monitor disk space and apply a reviewed retention/backup policy. Stop the watchdog for a consistent backup; preserve any WAL/SHM files together. Stop-monitoring intervals are recorded on restart.
- Service logs: `%ProgramData%\ShadowServerCommand\<Role>\logs`, JSON, daily/10 MiB rollover, 14-file retention. Windows Application Event Log sources `SSC.Agent` / `SSC.Watchdog` are created during installation. Event/log records exclude URLs, secrets and network exception text from delivery failures.
- Desktop status: `%ProgramData%\ShadowServerCommand\Status\watchdog.json`, atomic replacement; local Users have read-only access only to this sanitized status directory. The desktop on **AM5** reads it every 5 seconds. `open watchdog` or **Ctrl+W** shows real local service state, heartbeat, latency, failures, incident elapsed time, recent incidents, delivery queue and health. Amber indicates degraded/stale status; red indicates confirmed host/Minecraft outages. A snapshot older than 45 seconds is stale/unknown, not evidence of service health. Other dashboard metrics remain explicitly simulated.

```powershell
Start-Service SSC.Watchdog
Stop-Service SSC.Watchdog             # pauses only monitoring; never stops Minecraft
Restart-Service SSC.Watchdog
Get-Service SSC.Watchdog | Format-List *
./scripts/Get-SscStatus.ps1
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='SSC.Watchdog'} -MaxEvents 20
./scripts/Uninstall-SscService.ps1 -Role Watchdog  # confirmation required; preserves history/secrets
```

Use corresponding `SSC.Agent` commands on AM4. Uninstallation preserves files/secrets for audit and possible reinstallation; it never deletes a Minecraft directory. An upgrade requires stopping/uninstalling/reinstalling the service, retaining the data directory and correct secret ACLs. Rotate compromised/expired credentials and review old executable/firewall paths.

## Delivery reliability and honest observation

Monitoring and alert sending are separate workers. Discord outages cannot stall sampling. One scheduled outbox attempt runs every 2 seconds, with an 8-second HTTP timeout. Failed transient deliveries back off 5, 10, 20, 40, 80, 160, 300 seconds; every eighth failed attempt pauses for 30 minutes, then another bounded burst can occur. Pending alerts remain durable indefinitely. Rate-limit `Retry-After`/JSON `retry_after` delays are honored. Invalid webhook credentials/payload responses (400/401/403/404) place an alert in **Held** state for operator repair; queued recovery must not be mistaken for evidence that notification was delivered.

Stop the service before administrative queue commands. After repairing a secret:

```powershell
Stop-Service SSC.Watchdog
& "$env:ProgramFiles\ShadowServerCommand\Watchdog\SSC.Watchdog.exe" --retry-alerts
Start-Service SSC.Watchdog
```

FIFO ordering holds transiently delayed outage alerts ahead of later recovery alerts. Held alerts require operator repair and are not silently retried forever. Discord delivery is **at least once**, not exactly once: a request may reach Discord while its acknowledgement is lost. A stable event ID and original UTC event time accompany every message so rare ambiguous retries can be recognized. No mentions are permitted in payloads. The watchdog creates one initial alert per confirmed incident, one recovery, and at most one host-unreachable escalation for an existing agent incident. Repeated-stop warnings have a configurable time-window cooldown.

First failure observation, detection time, recovery time and elapsed duration are recorded separately. Watchdog restarts, delays exceeding 1.5 times the configured cadence and AM5 sleep/reboot generate monitoring-gap records with estimated missed scheduled intervals. Consecutive counters reset across a gap; active incidents are marked **observation interrupted**. A recovery after offline time reports elapsed incident time **including unobserved time**; it never claims the outage was continuously observed. No events are invented for a complete AM4 outage-and-recovery that happened while AM5 was offline. AM5's own power outage cannot be reported until it returns; use another independent monitor for that failure domain.

See [safe testing](WATCHDOG-TESTING.md) and [troubleshooting](WATCHDOG-TROUBLESHOOTING.md).
