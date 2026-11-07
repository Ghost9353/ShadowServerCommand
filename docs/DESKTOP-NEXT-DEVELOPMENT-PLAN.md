# Desktop development plan after R3

This is a separate proposed workstream. It does not change the R3 release, enable live controls, alter Minecraft, or authorize a production deployment. Begin implementation after the R3 package is reviewed; production acceptance of R3 still requires the documented AM4/AM5 checks.

## Current implementation

`SSC.Desktop` already has a WPF/MVVM shell, dependency injection, a local terminal, keyboard navigation, a phosphor-green theme, optional scanlines, filtering, and SQLite storage for simulated metric snapshots. Ten planned modules and the additional WATCHDOG page are accessible. Most module rows are static fixtures in `SSC.Core/Modules.cs`; `MainViewModel.RefreshAsync` uses the injected simulated telemetry source.

WATCHDOG is different: `IWatchdogStatusSource` reads the local AM5 service snapshot. It exposes the last authenticated AM4 heartbeat, host CPU/memory where available, process state, latency, failure count, incidents, monitoring gaps, and Discord delivery summary. `WindowsHeartbeatCollector` already collects host counters and a verified Minecraft process working set. A port accepting TCP connections is not proof that Minecraft is ticking. Working set is not Java heap.

Retain the existing independent AM5 Watchdog. Closing, crashing, or updating the desktop must not stop monitoring or Minecraft. Existing architecture/capability documents include historical Phase 1 descriptions; update those descriptions when a capability actually ships rather than treating them as evidence of current transport support.

## Delivery sequence

### 1. Make existing observed data useful in the desktop

Replace the overview and host-performance fixtures with presentation models derived from the existing AM5 snapshot. Keep an explicit simulation mode for demonstrations; select it deliberately and label every simulated view. Never combine fixtures with observed values in an apparently live total.

Show AM4 reachability, Agent health, Minecraft process status, protocol/server responsiveness, uptime, host CPU, host memory, Minecraft working set, and last sample time separately. Use the last authenticated measurement only as historical context after its freshness limit. For each value expose source, observation time, freshness, availability reason, and unit. Use `UNKNOWN`, `STALE`, or `UNAVAILABLE` when appropriate instead of zero or green status. Keep the current service-owned snapshot as the first integration boundary; the desktop must not become another outage detector.

Split the growing `MainViewModel` into module view models behind the existing Core interfaces. Replace the generic string-array rows with typed presentation models for metrics, incidents, and service state. Use asynchronous refresh with cancellation and bounded work; dispatch bound collection updates on the WPF thread. Preserve previous samples visibly as stale when reading fails, with a clear retry action and no blocking modal loop. Check that a normal AM5 user can read the sanitized status snapshot without receiving access to service secrets or write access to service configuration.

**Completion evidence:** Windows desktop launch, keyboard navigation, simulated/observed separation, stale/unavailable transitions, permission-denied recovery, and no effect on either service when the UI closes. View-model tests should exercise these behaviors rather than repeat property implementations.

### 2. Add read-only Minecraft capability discovery

Record the actual Java executable and Minecraft process identity, Java version, NeoForge version, and available telemetry adapters. The current collector supports either PID/executable/start time or an exact executable plus command-line marker; do not select an arbitrary `java.exe`. Query version/mod evidence read-only and label its provenance. A permission failure or ambiguous process match remains unknown.

Detect RCON, Spark, and companion support explicitly. Do not enable RCON, add mods, change `server.properties`, or restart the server as part of discovery. Any later adapter installation needs its own reviewed setup and maintenance procedure. Keep the existing pinned, mutually authenticated private transport; authorize and version any new read API rather than repurposing the heartbeat into a general command endpoint.

**Completion evidence:** capability results for absent, supported, incompatible, ambiguous, and inaccessible configurations, using fixtures or a separate test server. Actual AM4 discovery is a separate operator-approved validation step.

### 3. Introduce tick health, players, and history incrementally

Add a documented, version-tested Spark or companion adapter for TPS/MSPT only after confirming support for Minecraft 1.21.1 and the installed NeoForge build. Carry sample windows and timestamps with TPS and MSPT; do not derive them from CPU, player count, TCP acceptance, or an unrelated RCON response. Distinguish current, average, and percentile values only when the source supplies them. Java heap, GC, dimension timing, and loaded chunks remain unavailable until their own sources are verified.

Provide a player view from an authenticated supported source. A public status response may omit or truncate its player sample; display its count/sample accurately and never call it a complete roster. Persist session history only from a source with reliable join/leave identity and timing. Mark gaps in observation; do not fabricate logout times after a disconnect.

Append SQLite migrations for observed telemetry, capability provenance, player sessions, and query indexes. Preserve existing simulated history and its origin. Use bounded queries and reviewed retention settings; validate restart persistence and migration recovery. Plot timestamped host/tick history with gaps rather than connecting unobserved intervals as if measured.

**Completion evidence:** adapter contract tests, real protocol tests against a disposable compatible server, unavailable-data behavior, idempotent persistence, and migration from the existing schema. Production tick telemetry requires separate AM4 confirmation.

### 4. Deliver console, logs, alerts, and Discord visibility

Implement `IServerLogSource` as a read-only, bounded log tail/search adapter for explicitly configured files. Handle log rotation, truncation, encoding, malformed lines, reconnects, and backpressure without loading entire logs into the UI. Restrict paths on the Agent; do not accept arbitrary client file paths or shell text. Keep sensitive values out of transport, exported diagnostics, and rendered logs. Start with viewing and search; console command execution belongs to the later control milestone.

Feed the alerts/history modules from durable Watchdog incidents and delivery summaries, including pending, held, delivered, and last-error categories without webhook URLs. Read from a bounded service-owned projection or authenticated query, not a writable shared database. Show observation gaps and evidence that distinguish host outage, Agent failure, process stop, and possible unresponsiveness. Preserve the existing Discord outbox, deduplication, backoff, and independent service delivery. A desktop test-alert action must be typed, explicitly labeled, rate-limited, and audited; it must not expose or re-provision the webhook.

Audit the existing `MinecraftSecurityMonitor` before integrating it. Define an idempotent, read-only importer for a copy of `SecurityMonitor/connection-attempts.csv`, including timezone, encoding, malformed rows, duplicates, and provenance. Do not replace its behavior during dashboard work.

**Completion evidence:** rotation and bounded-search tests, sanitized exports, outbox/service-state presentation, duplicate-safe CSV import, and safe manual Discord test instructions. Loss of the desktop must leave delivery running.

### 5. Introduce administrative controls only after read-only operation is proven

Implement a small typed command allowlist behind `IServerController` with authenticated authorization, correlation IDs, replay protection, bounded timeouts, and durable audit outcomes. Start with harmless status operations. Reject arbitrary shell commands and unrestricted Minecraft command forwarding. Restrict any RCON bridge to local Agent use; never expose RCON or an administrative listener to the public internet. Keep credentials in the existing protected secret system.

Before start/stop/restart, establish the current launch mechanism, process ownership, backup procedure, graceful-stop behavior, and whether another supervisor owns recovery. Require action-specific confirmation for stop/restart and destructive player/world actions, showing the exact server and impact. Recheck state when executing; UI confirmation alone is not authorization. Add maintenance suppression with a visible expiry if planned stops would otherwise cause alerts. Bound crash-recovery attempts and escalate repeated failures rather than starting a restart loop.

**Completion evidence:** unauthorized/replayed/repeated requests rejected, identity mismatch rejected, confirmation required, graceful timeout behavior, crash-loop limits, and application shutdown independent of server shutdown. Exercise destructive paths on a disposable server, then schedule a separately authorized production maintenance test.

## Visual and accessibility requirements throughout

Keep the existing colors (`#030803`, `#66FF66`, `#82B982`, `#285C28`), rectangular thin borders, monospaced type, terminal navigation, and optional CRT effects. Use green for confirmed healthy state, amber for degraded/stale conditions, and red for critical incidents, with text and symbols so color alone is never the signal. Update the global simulation badge according to the active source, and keep source/freshness visible on mixed-capability views.

Retain Alt+1–0, Ctrl+W, Ctrl+L, F1, and F5 with visible focus, predictable tab order, screen-reader names, and ordinary text selection/copy. Persist the CRT preference locally, default it off for operational use, and provide a reduced-motion option for the boot/cursor animation. Validate normal and high DPI, keyboard-only operation, long log lines, a small supported window, and legibility without effects. Use virtualized controls for large logs, player lists, and histories.

## Proposed review gates

Ship the first read-only observed overview as its own PR and package after R3. Keep tick adapters, log/security import, and controls in separate changes with matching tests and documentation. Each delivery must state which data is observed, simulated, stale, unavailable, or awaiting an actual AM4 test. Do not make desktop readiness or a screenshot a substitute for successful service monitoring, authenticated heartbeats, or verified Discord delivery.
