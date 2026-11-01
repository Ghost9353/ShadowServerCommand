# Development report — SSC-MC v0.1.0 Alpha

Date: 2026-10-31. Scope: Phase 1, local simulation only.

## Delivered

- Nine-project Visual Studio solution: six application/library projects and three test projects.
- Compiled WPF desktop shell using MVVM, injected dependencies, Generic Host configuration and structured Serilog logging.
- Custom #030803 / #66FF66 theme, muted #82B982 labels, #285C28 borders, amber warnings and a red critical-alert resource. Monospaced text, rectangular panels, boot transcript, blinking cursor and optional scanlines (off by default).
- Ten navigable modules, keyboard shortcuts, local allowlisted terminal, module/log-fixture search and explicit offline/simulation provenance.
- Fixed demonstration dashboard metrics; unavailable TPS/MSPT and missing live capabilities carry explanatory reasons. Version/path information is labeled user supplied.
- Telemetry, capability, control, player/session, connection, alert, log and availability interfaces plus versioned communication DTOs. No remote transport or listener exists in this milestone.
- SQLite v1 transactional initialization, migration tracking, newer-schema rejection, parameterized snapshot writes and bounded history queries. Schema reserves incidents, sessions, connections and audit events. Desktop snapshots survive application restarts.
- Daily and 10 MiB JSON log rotation, retaining 14 files per role; safe configuration defaults and startup rejection of live operation settings.
- Separate service-compatible Agent and Watchdog scaffolds. They initialize local databases and log scaffold heartbeats only.
- Build/launch instructions, capability setup runbook, architecture/security design, Windows acceptance checklist and a Windows CI workflow.

## Executed validation

Environment: Linux x64; local .NET SDK 8.0.427; Release configuration. Initial sandbox attempts failed on .NET first-run/named-mutex setup; verified builds and tests ran with approved execution outside that sandbox. No Minecraft host was contacted.

| Check | Result |
| --- | --- |
| Full solution Release build, including WPF XAML/C# | PASS — 0 warnings, 0 errors |
| Unit suite | PASS — 9 passed, 0 failed, 0 skipped |
| Integration suite | PASS — 3 passed, 0 failed, 0 skipped |
| Security suite | PASS — 12 passed, 0 failed, 0 skipped |
| Agent scaffold process smoke check | PASS — schema v1, JSON logs, no live samples, graceful SIGTERM exit 0 |
| Watchdog scaffold process smoke check | PASS — schema v1, JSON logs, no live samples, graceful SIGTERM exit 0 |
| Framework-dependent Windows x64 desktop publish | PASS — `artifacts/desktop/SSC.Desktop.exe` produced |
| Windows WPF launch / rendered appearance / physical keyboard interaction | NOT RUN — Windows runtime unavailable |
| Windows CI workflow | ADDED — not executed in this workspace |
| Log size/day rollover behavior under load | CONFIGURED — not exercised |

Tests cover simulation provenance, unavailable tick metrics, cancellation, command parsing and allowlisting, navigation through every module via the presentation model, search/CRT commands, archive error recovery, idempotent migrations, reopen persistence, future-schema rejection, query limits, rejected live control and fail-closed live configuration. Presentation-model tests do not verify WPF rendering or actual keyboard event delivery.

Raw VSTest TRX output is in `TestResults/` in this workspace (ignored by source control). Smoke-check data and logs are disposable artifacts under `artifacts/smoke-data`, separate from desktop/server history.

## Limits and acceptance status

**Phase 1 implementation and Linux-verifiable checks are complete; full acceptance remains pending the Windows launch checklist.** A successful cross-build does not prove WPF launch or visual usability. No screenshot, Windows runtime success or real telemetry is claimed.

The app is an Alpha simulation, not an operational server controller. Agent/Watchdog have no live collection, probes, alerts or remote administration. RCON/Spark/companion detection is represented as not probed; setup is documented, not performed. Historical snapshot persistence is functional; incident/session/connection capture, report export and long-term retention policy are future work. Module data outside the local archive is fixture/static capability information. Sampling occurs at startup and on request, not continuously. CRT preference and module/filter state reset on launch. Multiple concurrent desktop instances are not an accepted deployment mode yet.

Authenticated/encrypted remote administration is a documented mTLS design requirement, not an implemented feature. Existing security monitoring was neither accessed nor replaced. No webhook secrets are present, and Discord delivery is not implemented. Local SQLite data is not encrypted at rest; apply a reviewed host/account/backup policy before storing sensitive production telemetry.

## Recommended next tasks

1. Run `scripts/verify.ps1` and `docs/WINDOWS-ACCEPTANCE.md` on Windows 11 x64. Capture launch evidence and resolve rendering/DPI/focus issues before marking Phase 1 accepted.
2. Audit the existing security monitor and a copied CSV sample; define a read-only, idempotent importer without changing the original monitor.
3. Add least-privilege Windows/process counters with explicit Java process identity, freshness and permission/error handling; keep missing metrics unavailable.
4. Implement actual capability negotiation for the chosen Spark/companion API before enabling TPS/MSPT or dimension metrics.
5. Implement private mTLS transport, identity/authorization, replay defenses and audit trails; provision OS-backed secrets. Keep public exposure blocked.
6. Add confirmed typed lifecycle operations with graceful-stop deadlines, backup safeguards and bounded crash recovery. Keep desktop shutdown independent.
7. Implement independent AM5 outage probes and Discord delivery with deduplication, timeout, rate-limit handling, bounded retries and recovery alerts.
8. Add incident/session capture, metric retention/aggregation and report export; test log rotation, concurrent access, schema upgrades and recovery on Windows.

The Windows deliverable in `artifacts/desktop` requires the .NET 8 Windows Desktop Runtime x64. Source and binaries are packaged separately for review; neither has been launched on Windows in this environment.
