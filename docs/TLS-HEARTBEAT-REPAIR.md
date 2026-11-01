# TLS heartbeat repair — AM4 10.10.10.2 / AM5 10.10.10.1

## Root cause and audit

The original Agent and Watchdog both imported their PFX with `X509KeyStorageFlags.EphemeralKeySet`. On Windows this requests a non-persisted private-key import (`PKCS12_NO_PERSIST_KEY`). Windows Schannel can fail to acquire that key for a TLS credential even when `HasPrivateKey`, RSA key size and .NET signing all work. Schannel Event 36869 in the Agent with the supplied evidence is consistent with this Windows credential-acquisition failure, rather than an absent private key in the PFX. Checking only `HasPrivateKey` was insufficient.

The repair uses **MachineKeySet on Windows**, without EphemeralKeySet, PersistKeySet or Exportable. This provides a machine key container that Schannel can acquire for the service. The certificate object owns the temporary imported container and retains it throughout the HTTPS/client lifetime; normal disposal releases it. PersistKeySet is intentionally omitted to avoid retaining a new key after each clean restart. On Linux the loader retains EphemeralKeySet, because OpenSSL supports it. Tests explicitly check the Windows CNG key is not ephemeral.

The existing PFX certificate bytes and SHA256 identity are unchanged. **Do not regenerate certificates or change pins for the ordinary repair.** Windows machine-key-container creation/acquisition rights are distinct from PFX-file read rights: if import fails under the service identity, the new stage/type/HRESULT logs identify that additional ACL/provider issue. Do not grant Everyone access to machine private keys.

The Agent certificate's missing SAN is not the cause under this application's existing **pinned-leaf trust policy**. Watchdog explicitly authenticates the exact configured SHA256 certificate, validity and TLS purpose; it does not use public-CA/hostname trust as its identity policy. A SAN does not repair Schannel's inability to acquire a private key. No fallback to anonymous TLS, accept-any certificates or disabled fingerprint verification was added. Mandatory client certificates, TLS 1.2/1.3, private binding and the AM5-scoped firewall rule remain in place.

Audit details:

- `MachineSecretStore.Read` still reads the named DPAPI blob from `%ProgramData%\ShadowServerCommand\Secrets`, decrypts with **LocalMachine** and no optional entropy, decodes UTF-8, and clears the decrypted byte array. No plaintext files are created; the short-lived password string is passed directly to PFX import. The role's configured secret name is retained. The earlier successful password/import/key-access evidence makes a wrong DPAPI password less likely; no secret reset is needed.
- `TlsCertificateLoader` checks private-key presence, validity, EKU/key usage and a real sign/verify operation. Existing certificates with correct purpose and no SAN remain accepted. Wrong passwords, public-only files, expired certificates, wrong EKU and missing DigitalSignature use fail closed.
- Agent owns its certificate until **after** WebApplication/Kestrel disposal. The HTTP client handler retains the client certificate; Watchdog disposes HttpClient/handler **before** the certificate. Both sides use the same platform-aware loader.
- Peer validation remains constant-time SHA256 pin comparison plus validity, now with explicit EKU/key-usage rejection reasons. CA-chain/name errors are diagnostic under pinned identity, never a reason to accept an unpinned peer.
- Agent startup failures log import stage, exception type and HRESULT before the generic console error. Kestrel HTTPS handshake diagnostics are enabled at Debug for its HTTPS namespace. Client/server validation logs include safe reason/policy/chain status codes. Watchdog distinguishes SecureConnectionError, timeout, invalid JSON, HTTP rejection and other transport categories. Logs omit exception messages/objects, passwords, PFX bytes and webhook URLs.

## Install the binary-only repair

Download `SSC-MC-tls-heartbeat-repair-win-x64.zip` from the repository's `downloads` folder. Extract it on **each** PC to `C:\SSC-TLS-Repair`. It contains only the repaired Agent/Watchdog publish directories, deployment scripts and this runbook; no production PFXs, configuration or secrets are shipped.

The following commands are for **elevated 64-bit Windows PowerShell**. They use the existing standard installation at `%ProgramFiles%\ShadowServerCommand`. For a custom service executable path, stop and review that path manually; the script deliberately refuses to guess. Existing .NET/ASP.NET Core Runtime 8 x64 prerequisites remain unchanged.

On **AM4 (10.10.10.2)**:

```powershell
Set-Location C:\SSC-TLS-Repair
.\scripts\Update-SscService.ps1 -Role Agent `
  -PublishDirectory C:\SSC-TLS-Repair\agent `
  -BackupDirectory C:\SSC-TLS-Repair\rollback-agent
Get-Service SSC.Agent
```

On **AM5 (10.10.10.1)**:

```powershell
Set-Location C:\SSC-TLS-Repair
.\scripts\Update-SscService.ps1 -Role Watchdog `
  -PublishDirectory C:\SSC-TLS-Repair\watchdog `
  -BackupDirectory C:\SSC-TLS-Repair\rollback-watchdog
Get-Service SSC.Watchdog
.\scripts\Test-SscTlsRepair.ps1 -TimeoutSeconds 120
```

Each update stops/restarts **only the named SSC service**, first backs up its existing binaries with an administrator-only ACL, and copies the repaired binaries into the existing directory. It preserves service identities/startup/recovery settings, firewall rules and everything in ProgramData: `service.json`, identity PFX, DPAPI files, databases, queued alerts and monitoring history. It never modifies, stops or restarts Minecraft. A previously stopped SSC service remains stopped; use Start-Service explicitly if that is intentional. Do not use the original install/uninstall scripts for this patch. Do not copy bundled disabled appsettings over ProgramData `service.json`.

Both Windows certificate imports need the repair; updating AM4 alone can leave the AM5 client using the old Schannel-incompatible import. The brief monitoring interruption is recorded as a gap, not a continuously observed outage.

## Verify the actual machines

`Test-SscTlsRepair.ps1` runs on AM5 and waits for a **new** status snapshot and heartbeat after the command starts. It succeeds only when the service is running, Computer is Online, LastHeartbeat is populated/fresh, failures are zero, and at least three consecutive authenticated/healthy checks have succeeded. It does not infer authentication from TCP success or the SCM Running state.

With default cadence, allow roughly 30–60 seconds after both services return. In the service logs, verify:

1. Agent and Watchdog each report `TLS certificate ready`, `storage=MachineKeySet`, the existing SHA256 fingerprint, signing algorithm/key size, validity and SAN presence.
2. Watchdog probe records change to `heartbeat=True`; status contains a measured heartbeat latency and populated UTC `State.LastHeartbeat`.
3. After three successes, the open agent incident has a `RecoveredAt` value and the log contains the queued recovery event ID. With a preexisting agent-unavailable incident, the event uses the existing `AM4 COMPUTER RECOVERED` label; it denotes heartbeat recovery and does not establish that the PC previously shut down.
4. Agent's Schannel 36869 events stop for new handshake attempts. Use an elevated AM4 shell to inspect recent events:

   ```powershell
   Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Schannel'; Id=36869; StartTime=(Get-Date).AddMinutes(-10)} -ErrorAction SilentlyContinue |
     Select-Object TimeCreated,Id,Message
   ```

5. Open the desktop on AM5, run `open watchdog` / Ctrl+W, and verify current status rather than a stale snapshot. The CLI validation does not depend on the desktop.

Safe local status/log inspection:

```powershell
Get-Content "$env:ProgramData\ShadowServerCommand\Status\watchdog.json" -Raw | ConvertFrom-Json |
  Select-Object WrittenAt,ServiceHealth,DeliveryStatus,LastDelivered,State
Get-WinEvent -FilterHashtable @{LogName='Application'; ProviderName='SSC.Watchdog'} -MaxEvents 20 |
  Select-Object TimeCreated,LevelDisplayName,Message
```

A wrong/expired pin or inaccessible signing key must remain an AgentUnavailable condition when other probes respond. Missing heartbeat never means Minecraft stopped. If all independent probes fail, the existing detector can report AM4 Unreachable with cause unknown. Verified process absence remains a separate Minecraft incident. These distinctions and recovery transitions are regression-tested.

## Confirm Discord still works without touching Minecraft

Discord production code and the encrypted webhook are unchanged. Queue/retry/rate-limit/payload tests pass, but a local test transport cannot confirm your channel.

On **AM5**, after heartbeat recovery, run one safe operator test:

```powershell
Set-Location C:\SSC-TLS-Repair
Stop-Service SSC.Watchdog
try {
    .\scripts\Test-SscDiscord.ps1
} finally {
    Start-Service SSC.Watchdog
}
.\scripts\Test-SscTlsRepair.ps1 -TimeoutSeconds 120
```

Check the designated Discord channel for the recovery event and **OPERATOR TEST**, with actual event IDs/times. The test command reports success only if Discord acknowledges that specific test ID. Existing pending FIFO alerts can leave it queued; do not enqueue many duplicates. Restore monitoring in the finally block. These commands never stop Minecraft. Keep the existing webhook secret; do not paste it into chat, command arguments, config or logs.

## Rollback

Restore **binaries only** using the backups printed during update. Keep ProgramData configuration, PFX files, fingerprints, DPAPI blobs and history intact. On the corresponding PC:

```powershell
# AM4 only
C:\SSC-TLS-Repair\scripts\Update-SscService.ps1 -Role Agent `
  -PublishDirectory C:\SSC-TLS-Repair\rollback-agent\previous-binaries `
  -BackupDirectory C:\SSC-TLS-Repair\before-agent-rollback

# AM5 only
C:\SSC-TLS-Repair\scripts\Update-SscService.ps1 -Role Watchdog `
  -PublishDirectory C:\SSC-TLS-Repair\rollback-watchdog\previous-binaries `
  -BackupDirectory C:\SSC-TLS-Repair\before-watchdog-rollback
```

If an update failed and left its service stopped, complete the restore from a confirmed complete backup, then explicitly `Start-Service SSC.Agent` or `Start-Service SSC.Watchdog`. Rollback reinstates the old ephemeral-import behavior and can reinstate the original TLS failure; preserve logs for investigation. No database migration is part of this repair, so no history downgrade or deletion is required.

## Replacement certificates — only if separately necessary

Normal deployment reuses both identities. If logs establish expiration, wrong EKU/key usage or corrupt/mismatched key material, plan coordinated replacement. The updated `New-SscCertificates.ps1` emits explicit RSA/SHA256, DigitalSignature + KeyEncipherment, server/client EKU, and SAN IP/DNS entries:

```powershell
.\scripts\New-SscCertificates.ps1 -OutputDirectory C:\SSC-Cert-Rotation `
  -AgentAddress 10.10.10.2 -WatchdogAddress 10.10.10.1
```

During a planned window, stop only the two SSC services, preserve each PC's current PFX/config/secrets securely, transfer only its own replacement PFX through a trusted channel, and replace the PFX at its **configured** path. Set AM4 `Heartbeat.TrustedClientSha256` to the new **Watchdog** SHA256 pin and AM5 `Watchdog.TrustedServerSha256` to the new **Agent** pin. Provision each new PFX password using the existing hidden `Set-SscSecret.ps1` prompt on the corresponding PC. Keep the Discord webhook unchanged. Apply both sides consistently before restarting; run the heartbeat and Discord verification above. Never log a password or relax validation during rotation. These optional rotation steps are not performed by the patch script.

## Evidence and limits

See TLS-REPAIR-REPORT.md for executed tests and the Windows CI URL. A Windows hosted runner validates Schannel and the production loader but does not establish key-container permissions under your two virtual service identities. Actual 10.10.10.2/10.10.10.1 status, live Event Log behavior and real Discord delivery after deployment require the commands above on those PCs. This cloud session has no VPN/remote Windows execution access and does not claim to have deployed to either machine.
