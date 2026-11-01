# SSC.Watchdog extension — development report

Date: 2026-10-31. Target: Windows 11 x64 AM4 + AM5. Development/verification: Linux x64, .NET SDK 8.0.427. This report supersedes the original Phase 1 scaffold descriptions for Agent/Watchdog only.

## Implemented

**AM4 Agent:** Windows-service-compatible ASP.NET Core heartbeat host, disabled until configured, explicit private IPv4 listener, TLS 1.2/1.3, mandatory pinned client certificate, no anonymous/public endpoint. The heartbeat reports actual uptime, agent health/version, nullable CPU/physical-memory usage, nullable Java working set and Minecraft process identity evidence. Automatic Java identity discovery uses exact executable path plus unique command-line marker; optional PID mode checks creation time to prevent PID reuse. Access failures/ambiguity are unknown, not fabricated process state. No Minecraft lifecycle control or filesystem modifications exist.

**AM5 Watchdog:** independent service, immediate startup reconciliation, default 15-second concurrent heartbeat/ICMP/agent-TCP/Minecraft-TCP/status checks, bounded probe deadlines and three-failure/three-success confirmations. It distinguishes host unreachable, agent unavailable, process stopped, port unavailable and potentially unresponsive Minecraft. A Minecraft Java status handshake detects missing application replies even when TCP accepts connections; status is not TPS/tick-health evidence. Causes of unreachability and intentional stop vs crash are explicitly unknown.

**Persistence/delivery:** separate transactional SQLite schema with state, raw samples, incidents, monitoring gaps and an atomic alert outbox. One initial event per confirmed incident, one recovery, at most one host-unreachable escalation, and cooled-down repeated-stop alerts. Internet failures leave durable pending events; bounded exponential retry bursts, rate-limit delay handling, permanent-failure hold/requeue and stable IDs limit spam. Discord mentions are disabled. Production requests use HTTPS and an exact Discord host/path allowlist with redirects disabled. Authentication failures, timeouts and HTTP responses are logged without webhook URLs or network exception text. At-least-once delivery can produce a rare duplicate if Discord accepts a request but its acknowledgement is lost; exactly-once external delivery is not claimed.

**Windows/deployment:** Automatic services under non-admin virtual service accounts, SCM restart actions, Windows Application Event Log integration, rotated JSON diagnostics, graceful normal shutdown, ProgramData role directories, DPAPI LocalMachine encrypted secrets plus restricted ACLs, certificate provisioning and scoped Private-profile firewall rule on AM4. Fatal watchdog worker failures exit nonzero for SCM recovery; pending transactions/history are recovered through SQLite. Installation scripts do not auto-start services before provisioning. Uninstall requires confirmation and preserves files/history/secrets.

**Desktop:** added WATCHDOG section without replacing the original ten modules; Ctrl+W/open watchdog; sanitized local AM5 status-file refresh every five seconds; amber degraded/stale and red critical conditions; heartbeat time/latency, failures, incident elapsed time, process/host state, recent incidents, delivery status, service health and basic host measurements. The other dashboard metrics remain Phase 1 simulation. A missing/stale snapshot is not shown as healthy.

## Executed validation

The full Release solution builds with **0 warnings and 0 errors**. Automated results:

| Suite | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Unit | 23 | 0 | 0 |
| Integration | 11 | 0 | 0 |
| Security | 21 | 0 | 0 |
| **Total** | **55** | **0** | **0** |

- Normal operation, heartbeat stopped, process stopped, unavailable game port, complete network/power-loss-equivalent evidence, three-success recovery and single-event deduplication passed with simulated observations.
- Reopening the durable database and reconciling a restarted/offline watchdog preserves incident/outbox state, resets confirmation streaks, records missed-monitoring gaps and marks continuity interrupted.
- A simulated outage generated and delivered one emergency Discord JSON payload through a recording HTTP test transport, followed by one recovery payload. Offline delivery, retry scheduling, 429 handling, held invalid credentials, requeue and stable-ID deduplication passed.
- Real loopback Kestrel HTTPS tests accepted the trusted client, rejected missing/wrong client certificates, and rejected a wrong server pin using the production endpoint/probe code.
- Real loopback TCP/status tests distinguish closed port, accepting-but-silent port and Minecraft status replies. No real Minecraft process was used.
- Desktop presentation-model tests verify actual watchdog-source rows, module selection and critical condition state. They do not verify WPF rendering.

An initial run exposed a missing-platform ICMP utility exception; the probe now degrades that check instead of stopping monitoring. Build-time issues were corrected before the successful results above. Local .NET commands required approved execution outside the workspace sandbox due to runtime synchronization restrictions. No actual AM4/AM5 host or real webhook was contacted by development tests.

Raw TRX results: `TestResults/watchdog-verified/`. Windows x64 framework-dependent builds are provided under `artifacts/agent`, `artifacts/watchdog`, `artifacts/desktop`; the deployment ZIP packages these role directories together with scripts, example configuration and guides. Published Windows binaries have not been launched on Windows here.

## Actual-computer tests still required

The operator explicitly chose **the secure test script on AM5** for actual Discord-channel validation. No production webhook was supplied to this environment; real Discord delivery is **not claimed tested**. Run the documented stop-watchdog / Test-SscDiscord / finally-start-watchdog sequence; Minecraft stays running. A success requires acknowledgement of that specific test event; verify the message in the designated channel as well.

Also pending on Windows: service installation/automatic no-login startup, virtual-account WMI/counter access, DPAPI/PFX ACL behavior, actual Event Log writes and SCM failure recovery, firewall profile/scope inspection, AM5 sleep/reboot reconciliation and WPF appearance/keyboard/DPI testing. These cannot be established by a Linux cross-build. Actual PC power-off/disconnect tests were not performed; use staging VMs or planned graceful maintenance, never an automated forced power-off.

## Operational limits and next steps

- Keep AM5 awake/powered. It cannot observe or send messages while offline; gaps are recorded on return. An outage entirely within that gap is unknown. Another independent host is needed to cover AM5 failure itself.
- Both hosts need correct clocks, compatible private networking, valid certificates and installed runtimes. Review certificate expiry and WMI/counter permissions before relying on monitoring.
- Configure process identity carefully; do not infer a specific server from java.exe alone. Expected maintenance currently alerts as a stop unless monitoring is deliberately coordinated; no lifecycle-intent API is implemented.
- Tick health remains unavailable without Spark/companion telemetry. Failed status replies can also mean status is disabled or filtered, hence "potentially unresponsive" wording.
- No automatic deletion of history/outbox records; review disk capacity, backup/retention and at-rest database sensitivity before long-term production use. Stop the service for administrative queue commands and consistent backups. Multiple concurrent watchdog processes sharing one database are not a supported deployment.
- Complete the real Windows/Discord acceptance steps in WATCHDOG-TESTING.md, then add maintenance-intent integration, certificate-expiry warnings and a reviewed archive retention policy as the next improvements. Existing MinecraftSecurityMonitor remains untouched.

Deployment: [WATCHDOG.md](WATCHDOG.md). Safe tests: [WATCHDOG-TESTING.md](WATCHDOG-TESTING.md). Troubleshooting: [WATCHDOG-TROUBLESHOOTING.md](WATCHDOG-TROUBLESHOOTING.md).
