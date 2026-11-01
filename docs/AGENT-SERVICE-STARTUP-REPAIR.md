# Agent Windows service startup repair

Target: existing AM4 Agent `10.10.10.2:27443`, virtual account `NT SERVICE\SSC.Agent`; compatible with the repaired AM5 Watchdog on `10.10.10.1`. This supersedes the first TLS repair package for the Agent. No Minecraft operation, certificate regeneration or credential change is performed.

## Findings and what is established

The original Agent and first TLS repair both loaded the PFX **before** `WebApplication.Build/RunAsync`, hence before `WindowsServiceLifetime` connects to SCM. The first repair additionally changed the import from EphemeralKeySet to MachineKeySet, added a signing probe and created a separate bootstrap logger. New native/key/logging work was therefore put on the critical pre-dispatcher path. Windows Events 7000/7009 establish a service-connect/start timeout; they do not identify which pre-dispatcher operation stalled.

The failed package was reproduced in a disposable Windows runner under **NT SERVICE\SSC.Agent**. It started and completed mTLS there. Consequently there is no evidence that MachineKeySet is universally blocked for that account. Broad key-directory ACL changes, increasing ServicesPipeTimeout, regenerating certificates or blaming missing runtimes are not justified by that result. AM4's precise blocking operation remains unproven without its new startup journal or a process trace.

The repair removes DPAPI/key import/signing from that pre-dispatcher path and removes the extra Agent bootstrap logger. Import remains MachineKeySet for Schannel compatibility; the existing PFX/certificate identity and pin remain unchanged. The SCM registration, service identity and UseWindowsService integration are retained. A diagnostic WindowsServiceLifetime subclass records the actual OnStart/SCM connection.

Kestrel option construction itself can happen during Build. For that reason options contain only a certificate selector; a hosted initializer, registered before the web-host service, performs actual loading **after SCM connects**. HTTPS is never declared ready without the initialized key, and the selector never falls back to an anonymous or unvalidated certificate. Import/signing is bounded to 20 seconds with a 30-second generic-host startup budget; failures emit a precise journal stage and nonzero exit rather than silently consuming the SCM connection window. These are application deadlines, not a global Windows timeout extension.

Independent `startup-*.jsonl` records are written immediately at process entry, before DI/Serilog, and flushed to local disk. They contain UTC time, PID, actual Windows identity, build module ID, elapsed milliseconds and safe error type/HRESULT. No secrets, passwords or exception messages are recorded. Rotation bounds files to 1 MiB with 14 retained journal files. The ordinary structured service logs remain in place.

## Corrected package

Download `SSC-MC-agent-service-startup-repair-win-x64.zip` and extract it on AM4 to `C:\SSC-Agent-Startup-Repair`.

The package contains the corrected Agent, the compatible existing TLS-repaired Watchdog, scripts and runbooks. **AM4 only needs the Agent update.** Do not update the functioning AM5 Watchdog unnecessarily. Its package is included if AM5 still needs the earlier TLS key-storage repair.

Every publish directory includes `deployment-manifest.json` with hashes of its complete file set. The updater verifies source role/required files/hashes before stopping anything, backs up the entire existing installation, creates a verified rollback manifest even for old installations, verifies copied hashes, and waits for a fresh **HttpsReady** event from the actual new service PID. It preserves ProgramData config, encrypted secrets, PFX files, database/history/outbox, service identity/start mode and firewall settings. Failed deployment/startup restores previous binaries and attempts to restart a previously running service. It reports the failure even after successful rollback; it never reports the patch as successful merely because SCM said Running.

## AM4 installation — elevated 64-bit PowerShell

The restored original service can remain running until this updater stops it. Check the correct extraction path and use a **new** backup directory:

```powershell
Set-Location C:\SSC-Agent-Startup-Repair
.\scripts\Update-SscService.ps1 -Role Agent `
  -PublishDirectory C:\SSC-Agent-Startup-Repair\agent `
  -BackupDirectory C:\SSC-Agent-Startup-Repair\rollback-am4
Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'" |
  Select-Object Name,State,StartMode,StartName,ProcessId,PathName
```

Expected: Running, Auto, `NT SERVICE\SSC.Agent`, a new PID and the existing executable path. The updater does not edit service registration or Windows startup timeout, does not import into a new Windows certificate store, and does not grant broad machine-key permissions.

If it fails, it should restore the earlier binaries. Confirm service state and start manually only if necessary:

```powershell
Get-Service SSC.Agent
Start-Service SSC.Agent  # only if restored but stopped
```

## AM5 live acceptance — leave Minecraft running

After the AM4 updater reports HttpsReady, use the verification script on **AM5**. Copy/extract the scripts there, or use the previous TLS package's identical verifier:

```powershell
Set-Location C:\SSC-Agent-Startup-Repair
.\scripts\Test-SscTlsRepair.ps1 -TimeoutSeconds 120
```

The command waits for a fresh status snapshot/LastHeartbeat after the verifier begins, zero failures and three consecutive successful authenticated heartbeats. Agent TCP reachability or SCM Running alone cannot pass this check. Confirm measured latency, populated LastHeartbeat and the recovered incident in logs/Discord. Existing DPAPI webhook delivery is unchanged.

If AM5 still uses its original pre-TLS-repair binaries, apply only then:

```powershell
.\scripts\Update-SscService.ps1 -Role Watchdog `
  -PublishDirectory C:\SSC-Agent-Startup-Repair\watchdog `
  -BackupDirectory C:\SSC-Agent-Startup-Repair\rollback-am5
.\scripts\Test-SscTlsRepair.ps1 -TimeoutSeconds 120
```

## If startup fails: collect the journal before another change

On AM4, no password or webhook output is needed:

```powershell
Get-ChildItem "$env:ProgramData\ShadowServerCommand\Agent\logs\startup-*.jsonl" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content
Get-WinEvent -FilterHashtable @{LogName='System';Id=7000,7009;StartTime=(Get-Date).AddMinutes(-10)} -ErrorAction SilentlyContinue |
  Select-Object TimeCreated,Id,Message
```

For the corrected process, the expected sequence is ProcessEntry → ConfigurationReady → HostBuildBegin → HostBuilt → **ScmConnected** → CertificateInitializationBegin → DpapiReadBegin/Complete → PfxImportBegin/Complete → SigningProbeBegin/Complete → CertificateInitializationReady → **HttpsReady**.

The account must be `NT SERVICE\SSC.Agent`. If the final stage is HostBuildBegin, inspect host/logging initialization rather than blaming MachineKeySet. If it reaches PfxImportBegin but not Complete, investigate the native provider or per-account machine-key import/creation, using the safe HRESULT and elapsed time. PFX-file read permission and machine private-key-container permission are different; do not grant Everyone access to Crypto folders. A wrong DPAPI secret or password should produce a bounded StartupFailed entry; correct the existing secret only after that evidence, not by resetting credentials blindly. Preserve logs and the exact package hashes.

Absence of ProcessEntry suggests the executable/host did not reach managed Main, wrong files/path, missing runtime or inability to write the role log directory; inspect the registered binary, hash manifest, .NET/apphost/Event Log and ACLs. Existing runtime installations do not prove that an individual DLL/runtimeconfig deployment matches the package.

## Explicit rollback

New backups include manifests, so the corrected updater can restore them:

```powershell
Set-Location C:\SSC-Agent-Startup-Repair
.\scripts\Update-SscService.ps1 -Role Agent `
  -PublishDirectory C:\SSC-Agent-Startup-Repair\rollback-am4\previous-binaries `
  -BackupDirectory C:\SSC-Agent-Startup-Repair\before-explicit-rollback -LegacyRollback
```

An original backup created by the previous updater may lack a manifest. First create one **only for a complete, trusted previous-binaries backup**; this records its current bytes, not proof of provenance. Then invoke the corrected updater with that backup as PublishDirectory:

```powershell
.\scripts\Write-SscDeploymentManifest.ps1 -Role Agent `
  -Directory C:\YOUR-EXISTING-BACKUP\previous-binaries
```

Original Agent binaries do not emit HttpsReady. When restoring that older version, the updater must use the explicit `-LegacyRollback` option shown above (only for a trusted previous-binaries directory); do not mistake lack of the new journal for a broken original service. Preserve config, PFXs and secrets; never reinstall or regenerate them solely for binary rollback.

## Scope of validation

See AGENT-SERVICE-STARTUP-REPORT.md for Windows CI evidence and test cases. The regression starts the real published service under its non-admin virtual account, performs mTLS, checks SCM-before-key ordering, executes the updater, verifies config/PFX/DPAPI bytes are unchanged, rejects tampered packages before stopping the service, and exercises failure recovery/rollback. It runs only on disposable Windows GitHub runners and refuses existing SSC installations.

Actual AM4 deployment and authenticated heartbeat with the operator's AM5 remain pending until the above live commands succeed. This cloud environment has no remote execution/VPN access to those PCs and never modifies Minecraft.
