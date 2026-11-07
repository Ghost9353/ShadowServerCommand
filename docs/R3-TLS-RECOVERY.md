# R3 staged TLS recovery — operator runbook

Candidate branch: `r3/tls-key-access-recovery`; PR https://github.com/Ghost9353/ShadowServerCommand/pull/3 . Do not interpret the PR or CI as approval/success of an actual AM4 deployment. No automatic production deployment is performed.

## What changes

R2 asks the virtual Agent account to import a private key into machine storage at every startup. Issue #2 establishes successful SCM connection and DPAPI decryption followed by `PfxImportBegin` → NTE_PERM `0x80090010`. Read access to the PFX does not grant permission to create machine private-key files. R3 was tested under that restriction for both Microsoft software CNG and legacy CAPI providers.

R3 separates **administrator preparation** from **service key use**:

1. Preparation reads the existing configured PFX and existing DPAPI password on AM4, checks its exact SHA256 against the existing public pin supplied from AM5, and imports the **same certificate and private key** into LocalMachine/My with persistent machine storage. No certificate/key material is regenerated; subject, SAN, validity, public key and certificate fingerprints stay the same.
2. It grants `NT SERVICE\SSC.Agent` **Read on the exact private-key file only**. It does not grant write/create rights to Crypto/Keys or RSA/MachineKeys, change service SID type, elevate the service, or weaken TLS/client authentication/pinning. Microsoft software CNG and the tested Microsoft legacy CSP storage layout are handled by actual provider information; RSACng can wrap a CAPI provider.
3. It adds an administrator-controlled public reference at `%ProgramData%\ShadowServerCommand\Credentials\Agent\identity.json`. This contains only certificate SHA256 and source PFX path/hash. Directory/file ACLs allow Administrators/SYSTEM control and Agent read only. All preexisting config/PFX/DPAPI/history files remain byte-for-byte unchanged.
4. At startup the Agent checks the source PFX path/hash, reads the pinned existing store credential, validates purpose/validity/key usage and signs/verifies a challenge. It performs **no PFX private-key import or DPAPI password decryption under the service account**. Store/key absence or denied read access fails closed, with bounded diagnostics; there is no ephemeral or writable-machine-key fallback.

The Agent remains a non-admin Windows service. Existing Kestrel mTLS, SHA256 pin policy, private bind/firewall, lifecycle/deadlines and logging are retained. This is an **Agent-only** package; AM5 Watchdog binaries/settings, Discord secrets and Minecraft are not changed.

## Prerequisites and package verification

Use the reviewed `SSC-Minecraft-Watchdog-R3-win-x64.zip` candidate and `R3-SHA256SUMS.txt` from the `R3-win-x64-<source-commit>` artifact of a successful branch workflow run linked in PR #3. Verify the ZIP's SHA256 with `Get-FileHash` against that published checksum, then extract to `C:\SSC-R3` on AM4. The artifact also contains a source ZIP; retain it for review. Workflow artifacts require GitHub sign-in and expire after 90 days; archive the reviewed ZIP/checksum locally. Retain the scripts folder together. The Agent is framework-dependent Windows x64 and uses the already-installed .NET/ASP.NET Core 8 x64 runtimes; no SDK is needed on AM4.

After verifying the ZIP checksum and extracting, run this **before preparation or updating**:

```powershell
# Compare this measured hash to the published R3-SHA256SUMS.txt from the same artifact.
Get-FileHash "$env:USERPROFILE\Downloads\SSC-Minecraft-Watchdog-R3-win-x64.zip" -Algorithm SHA256
Set-Location C:\SSC-R3
.\scripts\Test-SscR3Package.ps1 -Directory C:\SSC-R3
```

`release-manifest.json` records the exact source commit and SHA256 of every shipped file. `agent/deployment-manifest.json` is the updater's per-binary manifest. The external ZIP checksum anchors these manifests; this release is not Authenticode signed. Do not substitute files from older packages.

Before any update, record (do not dump secrets):

```powershell
Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'" |
  Select-Object Name,State,StartMode,StartName,ProcessId,PathName
```

The standard binary path must be `%ProgramFiles%\ShadowServerCommand\Agent\SSC.Agent.exe` for this updater. It refuses to guess a custom path. Keep a complete trusted backup of the currently working binaries; the updater also creates and verifies its own new backup. Existing sensitive-file backups must remain on the same protected AM4 machine, readable only to Administrators/SYSTEM; never upload a PFX, DPAPI blob or password to GitHub/chat.

## Stage 1 — obtain the existing public identity (AM5, read only)

This prints a **public fingerprint**, not a webhook or password:

```powershell
(Get-Content "$env:ProgramData\ShadowServerCommand\Watchdog\service.json" -Raw |
  ConvertFrom-Json).Watchdog.TrustedServerSha256
```

Copy that 64-hex value for the next command. Do not change AM5 configuration. If it is absent/invalid, resolve the intended existing trust identity before preparation. Do not substitute a newly generated certificate.

## Stage 2 — back up and prepare AM4 without stopping its working Agent

Before preparation, take a binary-only backup while the old service is still running (the updater later also takes its stopped, verified rollback backup). No ProgramData secrets need to leave their existing protected locations:

```powershell
$preparationBackup = 'C:\SSC-R3\before-preparation-binaries'
if (Test-Path $preparationBackup) { throw 'Choose a new backup directory.' }
New-Item $preparationBackup -ItemType Directory | Out-Null
& icacls.exe $preparationBackup /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'
if ($LASTEXITCODE) { throw 'Backup protection failed.' }
Copy-Item "$env:ProgramFiles\ShadowServerCommand\Agent\*" $preparationBackup -Recurse
.\scripts\Write-SscDeploymentManifest.ps1 -Role Agent -Directory $preparationBackup
```

Do not run another installer/updater concurrently. Preparation adds only the credential subtree/key described above; the actual update rollback snapshot remains `backup-before-r3\previous-binaries`.

Elevated 64-bit Windows PowerShell on AM4:

```powershell
Set-Location C:\SSC-R3
$existingServerPin = 'PASTE_THE_EXISTING_64_HEX_PUBLIC_PIN_FROM_AM5'
.\scripts\Prepare-SscAgentTls.ps1 `
  -AgentPublishDirectory C:\SSC-R3\agent `
  -ExpectedServerSha256 $existingServerPin
Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'" |
  Select-Object State,StartName,ProcessId
```

The preparation script verifies the package manifest before running the new executable's administrative preparation mode. It **never starts/stops the service or binds the server port**. Wrong password, wrong expected pin or unsupported provider fails before deployment; the existing Agent stays in place. Read the safe JSON result: `CertificateSha256` must equal the AM5 public pin, `Provider` identifies the key storage and `StoreEntryCreated` records whether the entry was new. Repeated preparation reuses the same identity; identity changes are refused.

Expected additive changes are a protected store credential/key read grant and the new public reference described above. Existing ProgramData configuration, encrypted secrets and PFX bytes are not overwritten. Preparation is not proof the service can use the key; only the following service/heartbeat stages establish that.

Unsupported hardware/third-party providers are not automatically granted guessed permissions or converted. Preserve the running installation and collect safe provider/stage/HRESULT output for provider-specific review. If preparation fails, do not proceed, do not grant Everyone Crypto-folder writes and do not reset certificates/secrets blindly.

## Stage 3 — approved AM4 update window

Only after preparation and operator approval of the staged rollout:

```powershell
Set-Location C:\SSC-R3
.\scripts\Update-SscService.ps1 -Role Agent `
  -PublishDirectory C:\SSC-R3\agent `
  -BackupDirectory C:\SSC-R3\backup-before-r3
```

Choose a **new** backup directory. The updater validates all package files/required DLLs and prepared-reference/source-PFX binding before stopping anything. It stops only SSC.Agent, creates/verifies rollback binaries and manifest, copies/verifies R3, and requires a fresh HttpsReady from the new actual service PID. It preserves SCM registration/identity/startup/recovery actions, firewall, existing data and all AM5 settings. Minecraft is never stopped, restarted or modified.

Expected journal stages now include `CredentialReferenceReadBegin/Complete`, `StoreKeyOpenBegin`, `SigningProbeBegin/Complete`, `StoreKeyOpenComplete`, `CertificateInitializationReady`, `HttpsReady`. A normal R3 service startup should **not** contain its own DpapiRead/PfxImport stages. Preparation and isolated diagnostics have separate PIDs/accounts, so filter by the actual Agent PID.

## Stage 4 — actual AM5 acceptance (required)

Copy/extract the complete R3 scripts folder on AM5; do not update/reconfigure/restart a functioning AM5 Watchdog. Run:

```powershell
Set-Location C:\SSC-R3
.\scripts\Test-SscTlsRepair.ps1 -TimeoutSeconds 120
```

Acceptance requires **fresh populated LastHeartbeat, zero consecutive failures, Computer Online and three consecutive authenticated healthy heartbeats** from the actual Watchdog. The verifier observes three distinct successful heartbeat timestamps after it starts, rather than accepting a preexisting Successes=3 counter. Review its measured latency, recovered incident and recovery alert. The verifier is read-only and independent of the desktop. Store preparation, private-key signing, local TCP connectivity, HttpsReady and green CI alone do not establish this acceptance criterion.

This cloud session cannot execute these steps on AM4/AM5 and has not done so. Do not call R3 production-successful until the actual AM5 checks pass. Discord delivery code/secret is unchanged; its existing configured delivery continues independently of the desktop. Confirm the real recovery message in the existing channel. If no outage/recovery event was generated, use the existing secure test on **AM5** (it reads the protected webhook locally; do not paste it into chat):

```powershell
# Optional controlled test: the existing test command requires exclusive outbox access.
# This briefly pauses only AM5 monitoring and records an observation gap.
$wasRunning = (Get-Service SSC.Watchdog).Status -eq 'Running'
try {
  if ($wasRunning) {
    Stop-Service SSC.Watchdog
    (Get-Service SSC.Watchdog).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
  }
  .\scripts\Test-SscDiscord.ps1
} finally {
  if ($wasRunning) { Start-Service SSC.Watchdog }
}
.\scripts\Test-SscTlsRepair.ps1 -TimeoutSeconds 120
```

This sends an explicitly labeled test without changing Minecraft, stopping the Agent, or changing the webhook. It temporarily pauses AM5 monitoring for exclusive outbox access and always attempts to restore its prior running state; verify the Watchdog is running afterward. See `docs/WATCHDOG-TESTING.md`. Actual channel delivery is a manual production check; CI uses local delivery test fixtures.

## Rollback and truthful recovery status

On update/startup failure, the updater waits for the service to stop, removes only incoming-manifest files absent from the prior backup, restores and re-hashes prior binaries, starts the prior service if it had been running, and **checks operational recovery**. Read `<BackupDirectory>\rollback-result.json`:

- `BinariesVerified`: prior files match the backup manifest.
- `ServiceRunning`, `ListenerReachable`, `HttpsJournalReady`: measured local checks.
- `OperationalVerified` and `VerificationLevel`: highest local level actually established.
- `AuthenticatedHeartbeatVerified`: deliberately false on AM4-only checks; AM5 must establish end-to-end authentication.

If the restored build previously had a ready journal, a **new** ready event for the restored PID is required. Older legacy builds can be checked only for Running + listener; the result says TLS unverified. A failed operational rollback is reported as failed, not as recovered merely because files copied. The regression explicitly tests that negative case, then repairs the fixture permission and confirms mTLS recovery.

Explicit binary rollback:

```powershell
Set-Location C:\SSC-R3
.\scripts\Update-SscService.ps1 -Role Agent `
  -PublishDirectory C:\SSC-R3\backup-before-r3\previous-binaries `
  -BackupDirectory C:\SSC-R3\before-explicit-rollback -LegacyRollback
```

Use `-LegacyRollback` only for a trusted previous-binaries directory whose original build lacks the ready journal. The helper preserves a prior intentionally stopped state. A stopped/failed restored service requires investigation/manual recovery; read the result and safe journal before repeated attempts. `Get-Service Running` alone does not prove authenticated monitoring.

The prepared certificate/key/reference are additive and left intact by binary rollback, so the original installation can run and R3 can be retried. Old binaries ignore the new reference. Do not delete an existing store key or revert global Crypto ACLs as a rollback shortcut. Any later cleanup must be separately reviewed using `StoreEntryCreated` and knowledge of other certificate consumers; automatic key deletion could corrupt another working installation.

## Minimal safe evidence if AM4 differs from CI

Collect only: service identity/start mode/PID/path; the new process's startup stages, error type/HRESULT; the preparation's provider label and **public** SHA256; rollback-result.json local status; and AM5's last evidence/LastHeartbeat/failure count. No PFX, DPAPI bytes, plaintext password/webhook, private key or full configuration is needed.

```powershell
$service=Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'"
Get-ChildItem "$env:ProgramData\ShadowServerCommand\Agent\logs\startup-*.jsonl" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content
```

Reference mismatch means re-evaluate the unchanged source identity before preparation. StoreKeyOpen/signing failure points to the existing provider/key read path, not a reason to enable writable machine-key import. No broad permission fallback is built in. The disposable CI fixture and its denied-directory harness must **never** be run on AM4/AM5; it refuses existing installations and is not included as an executable in the deployment package.
