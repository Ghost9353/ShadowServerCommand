#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$AgentPublishDirectory,[Parameter(Mandatory)][string]$OutputDirectory,
      [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceCommit)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$output=[IO.Path]::GetFullPath($OutputDirectory)
$stage=Join-Path $output 'package-root'
if(Test-Path $stage){throw 'Use a clean R3 package output directory.'}
New-Item "$stage\agent","$stage\scripts","$stage\docs" -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $AgentPublishDirectory '*') "$stage\agent" -Recurse
& "$PSScriptRoot\Write-SscDeploymentManifest.ps1" -Role Agent -Directory "$stage\agent" -CredentialMode ProvisionedMachineStore
foreach($script in @('Prepare-SscAgentTls.ps1','Update-SscService.ps1','Write-SscDeploymentManifest.ps1','Ssc-StartupJournal.ps1','Ssc-ServiceReadiness.ps1','Ssc-HeartbeatVerification.ps1','Test-SscTlsRepair.ps1','Test-SscR3Package.ps1','Get-SscStatus.ps1','Test-SscDiscord.ps1')){
    Copy-Item (Join-Path $PSScriptRoot $script) "$stage\scripts"
}
foreach($document in @('R3-TLS-RECOVERY.md','R3-ENGINEERING-REPORT.md','WATCHDOG-TESTING.md')){Copy-Item (Join-Path "$repo\docs" $document) "$stage\docs"}
@'
Shadow Server Command R3 - existing AM4 Agent update only

Read docs/R3-TLS-RECOVERY.md before executing anything.
Verify the ZIP SHA256 against R3-SHA256SUMS.txt from the same workflow artifact.
Then run scripts/Test-SscR3Package.ps1 against the extracted directory.
Preparation and update require elevated 64-bit Windows PowerShell 5.1 or later.
Requires existing SSC.Agent registration, unchanged PFX/DPAPI configuration and .NET/ASP.NET Core 8 x64.
No certificates/secrets are supplied or generated. No Watchdog binary change is needed.
Do not run the historical fresh-install/certificate-generation procedures for this update.
CI proves isolated Windows-service behavior; actual AM4 + three fresh AM5 heartbeats remain required.
Do not deploy to production without the operator's explicit authorization.
'@ | Set-Content "$stage\README.txt" -Encoding UTF8
$files=@(Get-ChildItem $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{Path=$_.FullName.Substring($stage.Length+1).Replace('\','/');Sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
})
[pscustomobject]@{Release='R3';SourceCommit=$SourceCommit;Files=$files} | ConvertTo-Json -Depth 5 | Set-Content "$stage\release-manifest.json" -Encoding UTF8
& "$stage\scripts\Test-SscR3Package.ps1" -Directory $stage
$zip=Join-Path $output 'SSC-Minecraft-Watchdog-R3-win-x64.zip'
Compress-Archive -Path "$stage\*" -DestinationPath $zip -CompressionLevel Optimal
$check=Join-Path $output 'archive-check'
Expand-Archive $zip $check
& "$check\scripts\Test-SscR3Package.ps1" -Directory $check
Write-Host ("R3 ZIP SHA256: {0}" -f (Get-FileHash $zip -Algorithm SHA256).Hash)
