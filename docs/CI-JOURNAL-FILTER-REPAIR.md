# CI startup-journal filter repair (issue #1)

Scope: PowerShell polling and CI fixture only. Agent/Watchdog C# code and published service binaries are unchanged from the previous startup repair. No production certificate, private key, fingerprint, encrypted secret, configuration or AM4/AM5 state is changed.

## Failure

The failed run was https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41351856687 . It recorded StartupFailed for PID 6488, CryptographicException/HRESULT 0x80070056, at `2026-11-01T21:26:19.4440345+00:00`; the fixture nevertheless timed out waiting for that record.

The predicate re-parsed `$_.TimestampUtc` with `[DateTimeOffset]::Parse`. PowerShell's JSON conversion can already produce a DateTime. Passing that typed object to the string-based Parse method coerces it to a formatted string, losing fractional seconds and potentially timezone information. On a UTC runner, a fresh event at 19.444 can become 19.000 and compare older than a start threshold within the same second. Crossing a second boundary made the earlier run pass, so one green run did not establish a stable fixture.

The diagnostic itself was present; it must not be removed, ignored or considered an Agent failure to emit diagnostics. Flush/rotation and PowerShell singleton-array behavior were audited as additional failure sources.

## Fix

- Shared `scripts/Ssc-StartupJournal.ps1` preserves JSON timestamps as strings when supported and directly converts typed DateTime/DateTimeOffset values without formatting them. UTC normalization retains ticks and timezone; ambiguous unzoned DateTime is rejected.
- The fixture snapshots existing process-entry keys before starting its intentional bad-password process. It selects the fresh ProcessEntry identity (PID, build and exact start timestamp), then requires **exactly one** StartupFailed from that process with **CryptographicException / 0x80070056**. Stale records and reused PIDs cannot satisfy it. A missing failure remains a hard test failure.
- The polling reader re-enumerates retained journal files each time, handles rotation, and permits concurrent file reads/rename. Only an incomplete final JSON fragment is deferred; complete malformed records are errors. The helper bounds parsed tails per file.
- Predicate diagnostics log selected PID/stage/file, original timestamp and type, exact normalized UTC timestamp, threshold and comparison result. The failure-start wall-clock threshold is diagnostic, not the sole process-freshness identity.
- The original encrypted fixture secret is restored in `finally` even if polling/assertion fails. The normal recovery, tamper rejection and automatic rollback tests remain enabled.
- The updater's HttpsReady polling and AM5 verification script used the same lossy Parse pattern. They now share the lossless reader/converter; no service/authentication behavior changes.

## Validation

The workflow adds deterministic journal tests in **PowerShell 7 and Windows PowerShell 5.1**. They cover same-second subsecond precision, JSON/DateTime/DateTimeOffset inputs, culture-independent parsing, UTC/local kind, ambiguous kind rejection, stale/reused PID selection, journal rotation, partial EOF, missing failures and complete malformed records.

The full Windows workflow also builds with zero warnings/errors, executes all 61 .NET tests, starts the real published Agent under NT SERVICE\SSC.Agent, verifies pinned mTLS, applies the production updater without changing config/PFX/DPAPI bytes, detects/restores the bad-password failure, rejects a tampered package before stopping the service, and verifies automatic rollback plus heartbeat after a nonlaunchable update.

**Full run: PASS** — https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41363555123 . Tested script commit: `30ca1a995fdc048890d9b7ee4499a169af033862`.

The run confirmed the original bug in PowerShell 7.6.9 and 5.1.26100.33648: `21:26:19.4440345+00:00` was coerced to `21:26:19.0000000+00:00` and the original predicate was False. The repaired real SCM fixture selected PID 3352 at `23:46:02.9467758+00:00` against threshold `23:46:02.6264945+00:00`, correctly True in the same second. All 61 .NET tests and every SCM/update/failure/recovery/tamper/rollback phase passed. Packages are published only after that green result.

## Updated package

The r2 package contains the same Agent/Watchdog publish files and deployment manifests, byte-for-byte, plus corrected PowerShell scripts including the new shared helper. Preserve the scripts directory together: Update-SscService.ps1 and Test-SscTlsRepair.ps1 now dot-source Ssc-StartupJournal.ps1.

Existing service configuration, key material and trust remain untouched. A healthy deployment does not need a new Agent binary or a service restart for this script-only correction. If you still need the earlier service-startup repair, use the r2 package and the existing AM4/AM5 startup runbook; all binaries and manifest checks are preserved. Never execute the disposable CI fixture on production computers.
