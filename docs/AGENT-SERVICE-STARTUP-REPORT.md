# Agent service startup repair report

Date: November 1, 2026 (America/Chicago). Target: existing AM4/AM5 installation. Minecraft, production certificate identities, authentication, encrypted secrets and configuration are unchanged.

## Audit and diagnosis

The original and first TLS-repair Agent loaded its certificate before constructing/running the generic host. The TLS repair added MachineKeySet import, a signing probe and a separate bootstrap logger on that pre-SCM path. Events 7000/7009 identify a startup/dispatcher timeout, but cannot identify the blocked native operation. No claim is made that the supplied AM4 evidence proves a particular Crypto-folder ACL, provider failure or binary mismatch.

The failed package itself was tested under `NT SERVICE\SSC.Agent` on a disposable Windows runner and successfully started/authenticated: https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41347178410 . This demonstrates that MachineKeySet can work for the virtual account with default machine permissions; it does not reproduce AM4's environment-specific stall. Broad permissions changes, new certificates and longer global ServicesPipeTimeout were therefore not introduced.

A confirmed design defect was corrected: native credential work could exhaust SCM's pre-dispatcher connection window without a useful independent diagnostic. The correction keeps UseWindowsService and its identity/name, records actual SCM OnStart, and moves credential loading to an asynchronous hosted initializer after SCM connects and before HTTPS starts. Kestrel options only register a certificate selector during Build; an intermediate regression caught that loading inside the options callback still ran too early. No lazy key import runs during option construction now.

The private-key task has a 20-second application deadline; generic-host startup has a 30-second budget after managed startup begins. This is not a global Windows timeout adjustment. Initialization failure writes its exact stage and safe HRESULT, and the process exits nonzero. An independent rotated/flushed journal starts at ProcessEntry and survives ordinary logger initialization failure. Startup can now distinguish host construction, SCM dispatch, DPAPI, PFX import, signing and HTTPS readiness. Account/PID/build ID are recorded without secret values or raw exception messages.

The updater previously copied/merged binaries and then checked SCM Running, with no complete package hash verification or readiness proof. It now validates a per-role full-file manifest before stopping anything, verifies the copied installation, creates a verified rollback manifest for old backups, requires a fresh HttpsReady journal from the new Agent PID, and restores previous binaries/restarts the original on failure. Existing service registration, startup mode, PFX/config/DPAPI bytes and ProgramData history are retained. Explicit `-LegacyRollback` is restricted to trusted previous-binaries directories and permits restoring old builds that do not emit the new journal; it does not relax application TLS authentication.

## Executed validation

Windows SCM regression: https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41351245533

Tested code/script commit: `a266db18724edf69f0680e0378891c2cdbdefe20`.

| Check | Result |
| --- | --- |
| Full Release build, Linux | PASS — 0 warnings / 0 errors |
| Full Release build, Windows | PASS — 0 warnings / 0 errors |
| Unit / integration / security suites on Linux | PASS — 23 / 17 / 21, total 61 |
| Unit / integration / security suites on Windows | PASS — 23 / 17 / 21, total 61 |
| PowerShell deployment syntax | PASS on Windows |
| Real published Agent registered with SCM | PASS — virtual account NT SERVICE\SSC.Agent, Automatic startup |
| SCM-before-key ordering | PASS — independent journal proves connection before DPAPI/PFX/signing |
| MachineKeySet key creation/use under the virtual account | PASS — signing completes and real mTLS heartbeat succeeds |
| Published Agent update using production updater | PASS — copied hashes/startup/readiness verified |
| Config/PFX/DPAPI preservation during update | PASS — before/after byte hashes unchanged |
| Wrong PFX password startup | PASS — bounded StartupFailed with CryptographicException/HRESULT, then successful recovery after secret restoration |
| Tampered package | PASS — rejected before healthy service is stopped, same PID retained |
| Hash-valid but nonlaunchable runtime configuration | PASS — failed update automatically restores previous binaries/service and mTLS heartbeat |
| Windows x64 Agent and compatible Watchdog publish | PASS — framework-dependent outputs produced |

Measured Windows runner results: initial cold native process/SCM start approximately 24.3 seconds (includes Windows process loading before managed Main); managed journal reached SCM within about 0.3 seconds and HTTPS readiness within about 0.45 seconds. These are observations from that runner, not promised timings for AM4. Later tested process startups reached HTTPS readiness within roughly 0.4 seconds of managed entry. Bad-password failure was recorded within roughly 0.33 seconds of managed entry, far before the reported 90-second timeout.

The SCM regression creates isolated test certificates/secrets only on a disposable GitHub Windows runner. It refuses existing SSC installations and never runs on the operator PCs. Actual TLS uses a pinned client certificate and SHA256 server pin; no anonymous/accept-any fallback is used. Intentionally corrupt/nonlaunchable test packages are not shipped in the deployment package. The unchanged protocol tests still exercise the repaired Watchdog probe, pin rejection, agent-vs-host/Minecraft distinction, LastHeartbeat/recovery and Discord outbox behavior.

## Package and deployment

Use `SSC-MC-agent-service-startup-repair-win-x64.zip`. It includes the corrected Agent, a compatible Watchdog publish directory for sites that still need the earlier client TLS repair, verified deployment manifests, update/rollback helpers, live heartbeat verification and runbooks. AM4 is the required update; leave a functioning repaired AM5 Watchdog alone.

Follow [AGENT-SERVICE-STARTUP-REPAIR.md](AGENT-SERVICE-STARTUP-REPAIR.md) for explicit AM4 update, AM5 verification, independent log collection and rollback. Runtime prerequisites remain .NET 8 / ASP.NET Core 8 x64. No SCM timeout registry edit, key-folder ACL broadening, certificate-store migration, production certificate regeneration or secret reset is required by this package.

## Outstanding live acceptance

The cloud environment has no remote execution/VPN access to AM4 or AM5. Their actual binaries, security tooling, key provider/container ACLs and startup traces cannot be inspected here. The precise original AM4 blocking operation therefore remains unconfirmed. The corrected package removes the newly added pre-dispatcher native-work exposure and provides the diagnostics needed if an AM4-specific failure persists.

Acceptance on the actual PCs remains pending: AM4 updater succeeds under its existing virtual identity, HttpsReady is fresh for its new PID, and AM5 reports fresh authenticated LastHeartbeat, zero failures and three successful checks. Inspect recovery evidence/Discord as documented. A real SCM/virtual-account test passed in CI; that is stronger than the prior console-only TLS tests, but is not a claim that AM4 has already been updated successfully.

## Subsequent CI polling correction

A later run exposed an intermittent PowerShell timestamp-filter defect in the wrong-password fixture even though StartupFailed was present. See [CI-JOURNAL-FILTER-REPAIR.md](CI-JOURNAL-FILTER-REPAIR.md) for the confirmed subsecond-loss cause, corrected scripts, and full green run. Agent/Watchdog binaries are unchanged; r2 includes the corrected shared polling helper and scripts.
