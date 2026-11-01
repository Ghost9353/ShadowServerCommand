# SSC TLS heartbeat repair report

Date: November 1, 2026 (America/Chicago). Scope: Agent/Watchdog TLS only; no Minecraft control, configuration migration, secret rotation or unrelated feature changes.

## Finding and repair

Both services imported PFX private keys with EphemeralKeySet. The supplied AM4 evidence (Schannel 36869, accessible RSA private key in .NET, service running and TCP reachable) fits Schannel's inability to acquire a non-persisted imported key for a TLS credential. A missing SAN is not a rejection cause in the existing SHA256-pinned trust policy. Accessible .NET RSA signing is necessary but insufficient evidence that Schannel can acquire the TLS key.

The shared loader now uses **MachineKeySet on Windows** and keeps the certificate/private-key owner alive throughout the HTTPS or client lifetime. It intentionally omits PersistKeySet and Exportable in production; normal disposal cleans up the temporary machine-key import. Existing certificate bytes, SHA256 fingerprints, PFX passwords, DPAPI blob names/scope and service identities are preserved. TLS client authentication and pin validation remain mandatory. New validation checks purpose/key usage and logs safe stage/type/HRESULT, peer rejection and heartbeat failure categories. Kestrel HTTPS handshake diagnostics are enabled.

Audit found no premature certificate disposal: Agent disposes Kestrel before its certificate, and Watchdog disposes its handler before its certificate. The shared loader addresses import/storage semantics, rather than masking the error with certificate regeneration. On Windows, private-key-container rights must still be verified for the actual virtual service identities; access to the PFX file alone does not establish those rights.

Files changed: Agent startup/HTTPS binding; Watchdog startup diagnostics; Infrastructure loader, peer validation, heartbeat diagnostics and logging configuration; TLS integration tests; future-certificate generator (SAN/EKU/key usage); additive binary-update/rollback and heartbeat-verification scripts. The detector, Discord delivery, telemetry schema and production settings/secrets are unchanged.

## Executed tests

| Check | Result |
| --- | --- |
| Linux x64 full Release solution build | PASS — 0 warnings, 0 errors |
| Linux unit / integration / security tests | PASS — 23 / 17 / 21, total **61**, zero failed/skipped |
| Windows GitHub Actions full Release solution build | PASS — 0 warnings, 0 errors |
| Windows unit / integration / security tests | PASS — 23 / 17 / 21, total **61**, zero failed/skipped |
| Windows x64 Agent publish | PASS — framework-dependent package produced |
| Windows x64 Watchdog publish | PASS — framework-dependent package produced |
| AM4/AM5 live deployment | NOT RUN — no remote execution/VPN access |
| Live Discord test after patch | NOT RUN — operator must verify on AM5; existing success supplied by operator |

Windows evidence: https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41344843602

Tested source commit: `7ebfa3c496e4ef4f9268dee850b5177e6a643841`. Deployment scripts and their syntax-validation workflow were added subsequently without changing the tested production C#. The follow-up Windows run also passed all 61 tests and PowerShell syntax validation: https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41345469126 . Actual installation/update/rollback script execution under the operator service identities remains pending.

Tests cover production loader selection, non-ephemeral Windows CNG keys, preservation of identity with and without SAN, signing-key usability after loader return, wrong-password/public-only rejection, wrong EKU and expired peers, trusted mutual TLS on real loopback Kestrel, missing/untrusted clients, wrong server pin, safe diagnostic redaction, heartbeat recovery populating LastHeartbeat after three successes, and correct AgentUnavailable classification with network/game-port reachability. Existing tests preserve host-unreachable versus Minecraft-process-failure separation, outage/recovery history, outbox retry/rate-limit behavior and Discord JSON serialization.

The prior Windows CI failure was also audited: its TLS fixture attempted to re-export a non-exportable test key and failed before the handshake. The fixture now permits export only for generated test certificates and loads actual server/client TLS credentials through the production loader. Permissive test-only server callbacks were replaced with pinned validation.

## Delivered deployment and limits

`SSC-MC-tls-heartbeat-repair-win-x64.zip` contains the repaired Agent/Watchdog, binaries-only update and rollback support, safe Discord test and a live heartbeat verifier. The update leaves ProgramData configuration, PFX files, DPAPI secrets, SQLite history and alert outbox untouched and only restarts the selected SSC service. It never touches Minecraft. Existing .NET 8 x64/ASP.NET Core 8 x64 runtime requirements remain.

Run the explicit AM4/AM5 commands in [TLS-HEARTBEAT-REPAIR.md](TLS-HEARTBEAT-REPAIR.md). AM5's verifier waits for a fresh status/LastHeartbeat, zero failures and three consecutive successful authenticated heartbeats. Inspect recovery evidence and Schannel logs, then send one OPERATOR TEST through the preserved webhook. The verifier reports failure rather than inferring authentication from TCP or service status.

Hosted Windows tests confirm Schannel compatibility for the repaired loader and test identities, not successful installation under NT SERVICE\SSC.Agent/SSC.Watchdog on the operator's PCs. They do not establish that Event 36869 has stopped on AM4, that the existing production pins match, or that a real Discord message was delivered after this update. Those machine-specific acceptance checks remain explicitly pending until the operator runs the provided commands.

No certificate replacement is required by the observed import defect. If separate validity, EKU, provider or key-integrity errors require rotation, the runbook describes coordinated new SAN/EKU/key-usage certificates and both trust-pin updates. Such rotation is not performed automatically.
