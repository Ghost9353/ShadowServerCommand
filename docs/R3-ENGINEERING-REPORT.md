# R3 engineering report

Date: November 7, 2026 (UTC). Issue #2: https://github.com/Ghost9353/ShadowServerCommand/issues/2 . Branch `r3/tls-key-access-recovery`; PR https://github.com/Ghost9353/ShadowServerCommand/pull/3 . No production deployment or main-branch merge is performed.

## Root-cause evidence

AM4 evidence establishes SCM connection (173 ms), successful DPAPI read, then failure inside `new X509Certificate2(..., MachineKeySet)` before PfxImportComplete with 0x80090010/NTE_PERM. The account can modify the source PFX but the reported machine-key directories allow no creation. That is consistent with denied machine-key creation, not proof of a wrong password or missing runtime. SERVICE_SID_TYPE NONE is not assumed to be the cause; R3 uses the existing virtual account's actual user SID.

A disposable Windows-service matrix reproduced **the exact NTE_PERM HRESULT** for both software CNG and CAPI when CreateFiles is denied only to NT SERVICE\SSC.Agent on the machine-key directories. Other identities/default directory ACLs are preserved and test ACLs restored in finally. This is a controlled reproduction of the failure class; it is not a trace of the actual AM4 machine or its exact production provider.

| Strategy under restricted virtual account | Import | Signing | Real service HTTPS mTLS |
| --- | --- | --- | --- |
| Per-start MachineKeySet, CNG | NTE_PERM 0x80090010 | unavailable | failed |
| Per-start MachineKeySet, CAPI | NTE_PERM 0x80090010 | unavailable | failed |
| EphemeralKeySet, either provider fixture | succeeded | succeeded | failed |
| Administrator-prepared persistent key, exact service read grant | no service import | succeeded | succeeded for both providers |

Ephemeral signing/import success was not adopted as a TLS solution. The selected strategy has actual SCM/virtual-account/Kestrel/mTLS evidence. A provider-specific discovery mattered: Microsoft legacy CSP keys can be exposed as RSACng through NCrypt. Managed RSA type alone incorrectly identifies their file layout; R3 maps the actual tested Microsoft provider to Crypto/Keys or RSA/MachineKeys. Unknown/hardware providers fail rather than receiving broad/guessed ACLs.

## Implementation

- New `AgentMachineCredential`: admin-only same-identity persistent import, expected existing SHA256 check, exact key-file Read grant, Administrators-owned protected public source reference, reparse rejection and atomic publication; service read-only store lookup, source PFX path/hash binding, key signing and purpose/validity validation.
- `AgentCertificateOwner` uses the prepared credential on Windows after SCM connects. Existing deadlines, ownership/disposal, Kestrel client authentication and pinning remain. The Agent no longer creates machine key containers or decrypts PFX passwords at service startup.
- `TlsCertificateLoader` shares its existing signing/purpose validator; default file import remains unchanged for other callers. No AM5 configuration/binary deployment is included.
- `Prepare-SscAgentTls.ps1` verifies package integrity and runs preparation without stopping/listening as the service. It requires the existing public AM5 server pin and preserves preexisting ProgramData bytes.
- `Update-SscService.ps1`, `Ssc-ServiceReadiness.ps1`, `Write-SscDeploymentManifest.ps1`: prepared-reference preflight, verified backup/deployment, PID/creation-time-safe readiness and measured rollback results even when copy/hash restoration fails, shutdown waiting, and removal of only update-introduced files. Original operational recovery is verified rather than assumed; local checks never claim authenticated AM5 proof.
- `tests/Windows/AgentServiceStartup.ps1`, isolated `tests/SSC.TlsServiceHarness`, workflow matrix: real service tests with two key providers and restricted creation, plus import/sign/HTTPS comparisons, restart, bad-password preflight, wrong expected pin, key read denial, missing-store-certificate startup/recovery, hash tampering, successful rollback and honestly reported unsuccessful rollback. Preparation and the successful update execute under Windows PowerShell 5.1; AM5 verification has 5.1/7 deterministic freshness regressions.

No domain schema, Minecraft files/processes, production certificates/secret values, AM5 settings, firewall exposure, TLS trust weakening or administrative network control was added. Preparation necessarily adds a protected persistent copy of the existing key and a public reference; it does not regenerate key material/certificate identity or alter the original PFX/config/DPAPI files.

## Executed tests and qualification

The complete solution builds on Linux and Windows with zero warnings/errors. Existing suites: unit 23, integration 17, security 21; **61 passed** on Linux and in each Windows provider job. PowerShell 5.1/7 journal tests remain green.

The Windows real-service matrix additionally confirms:

- Existing virtual account + automatic startup registration; SCM before credential loading.
- Exact NTE_PERM reproduction for per-start machine import under restricted directory creation.
- Ephemeral import/signing success does not imply successful service HTTPS.
- CNG and CAPI prepared keys sign and serve real pinned mTLS with creation still denied.
- Three consecutive authenticated loopback checks, restart, and config/PFX/DPAPI/reference byte preservation.
- Wrong expected public pin and wrong password fail preparation without disturbing the running Agent.
- Read-only service access to the exact key and reference; deliberate key-read denial fails with a safe native diagnostic, and ACL restoration recovers mTLS.
- Tampered package rejected before stop; failed update restores prior working service; deliberately unhealthy restored service yields OperationalVerified=false, followed by tested recovery after permission restoration.

Green provider runs before the final rollback-reporting enhancement: https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41389300631 and https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41389768241 . Prior full matrix including failed-operational-rollback reporting: https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41390115249 (PR) and https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41390110436 (push). The later pre-finalization code/script commit `8144cc7ceac294b824218ad1385891503bd91ff2` also passed both providers, including PowerShell 5.1 preparation: https://github.com/Ghost9353/ShadowServerCommand/actions/runs/41391314570 .

Finalization additionally hardens reference ownership/atomic publication, requires three distinct fresh AM5 observations, checks exact NTE_PERM, adds missing-certificate recovery, and improves rollback reporting/file cleanup. An initial finalization push exposed an existing Minecraft-status fixture's 500 ms deadline expiring on one hosted runner; its unchanged assertions now use a five-second test-only deadline for first-use JIT/scheduling. No production probe timing changed. The complete current-commit Windows matrix must execute successfully before packaging. The `package-r3` job depends on both provider jobs, publishes the Agent from that same commit, and verifies the complete extracted ZIP using Windows PowerShell 5.1. The release manifest records that exact source commit. PR #3 contains the final run links, artifact URL, measured checksums and review status; do not use a previous green run as evidence for an untested later commit.

These are controlled Windows runner tests, including real OS crypto permissions and real HTTPS, not actual AM4 deployment. The three checks are accelerated loopback protocol checks, not the operator's AM5 Watchdog completing its 15-second schedule. Production success is explicitly unverified until the staged runbook yields three fresh authenticated AM5 heartbeats. Exact AM4 provider/container state remains an operator verification item; no CAPI2 informational event alone is treated as proof.

## Delivery and live acceptance

R3 is the Agent-only `SSC-Minecraft-Watchdog-R3-win-x64.zip` Windows x64 framework-dependent candidate ZIP, with full per-file manifest (CredentialMode=ProvisionedMachineStore), SHA256 checksum file, source ZIP, release-level integrity manifest and staged preparation/update/rollback instructions. See [R3-TLS-RECOVERY.md](R3-TLS-RECOVERY.md). The dedicated branch/PR stays separate from main. No test harness, fixture certificate or secret is shipped in the runtime ZIP.

Existing .NET/ASP.NET Core 8 x64 runtimes are required. Preparation is additive, identity-checked, least-privilege and does not stop the existing service. Updating/restoring happens only when the operator explicitly runs the updater. All existing ProgramData data/config/PFX/DPAPI bytes and AM5 settings are preserved. Prepared credentials are retained on binary rollback for safety; automatic deletion of potentially shared keys is intentionally avoided.

Actual AM4 service startup/signing/readiness and the real AM5 three-heartbeat criterion remain pending. This cloud session has no VPN/Windows execution access and does not claim to have applied the candidate to either PC. The acceptance boundary is successful operator deployment plus AM5 verification, not compilation or green CI.
