#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='Medium')]
param([Parameter(Mandatory)][ValidateSet('Agent','Watchdog')][string]$Role,
      [Parameter(Mandatory)][string]$PublishDirectory,[string]$BackupDirectory,[switch]$LegacyRollback)
$ErrorActionPreference='Stop'
. "$PSScriptRoot\Ssc-StartupJournal.ps1"
. "$PSScriptRoot\Ssc-ServiceReadiness.ps1"
function Assert-Package([string]$Directory) {
    $manifestFile=Join-Path $Directory 'deployment-manifest.json'
    if(-not(Test-Path $manifestFile)){throw 'Missing deployment-manifest.json; use a complete corrected package or a verified rollback backup.'}
    $manifest=Get-Content $manifestFile -Raw | ConvertFrom-Json
    if($manifest.Role -ne $Role -or -not $manifest.Files){throw 'Wrong-role or empty deployment manifest.'}
    foreach($file in $manifest.Files){
        $full=[IO.Path]::GetFullPath((Join-Path $Directory $file.Path))
        if(-not $full.StartsWith($Directory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path escapes the package.'}
        if(-not(Test-Path $full -PathType Leaf) -or (Get-FileHash $full -Algorithm SHA256).Hash -ne $file.Sha256){throw "Missing/mismatched package file: $($file.Path)"}
    }
    foreach($required in @("SSC.$Role.exe","SSC.$Role.dll","SSC.$Role.deps.json","SSC.$Role.runtimeconfig.json","SSC.Infrastructure.dll")){
        if($manifest.Files.Path -notcontains $required){throw "Required deployment file missing from manifest: $required"}
    }
    return $manifest
}
$name="SSC.$Role";$target=Join-Path $env:ProgramFiles "ShadowServerCommand\$Role"
$source=(Resolve-Path $PublishDirectory).Path.TrimEnd('\')
if($source -eq $target.TrimEnd('\')){throw 'Publish source must differ from the running installation.'}
if($LegacyRollback -and (Split-Path $source -Leaf) -ne 'previous-binaries'){throw 'LegacyRollback is restricted to an explicit previous-binaries backup.'}
$manifest=Assert-Package $source # Before stopping anything.
if($Role -eq 'Agent' -and $manifest.CredentialMode -eq 'ProvisionedMachineStore'){
    $referencePath=Join-Path $env:ProgramData 'ShadowServerCommand\Credentials\Agent\identity.json'
    if(-not(Test-Path $referencePath)){throw 'Prepare the existing Agent key with Prepare-SscAgentTls.ps1 before stopping/updating the working service.'}
    $reference=Get-Content $referencePath -Raw | ConvertFrom-Json
    $configuration=Get-Content (Join-Path $env:ProgramData 'ShadowServerCommand\Agent\service.json') -Raw | ConvertFrom-Json
    if($reference.SourcePfxPath -ne $configuration.Heartbeat.CertificatePath -or (Get-FileHash $reference.SourcePfxPath -Algorithm SHA256).Hash -ne $reference.SourcePfxSha256){throw 'Prepared credential does not match configured PFX. Existing service was not stopped.'}
}
$service=Get-Service $name -ErrorAction Stop
$installed=Get-CimInstance Win32_Service -Filter "Name='$name'"
if($installed.PathName.Trim('"') -ne (Join-Path $target "$name.exe")){throw 'Nonstandard executable path; inspect registration before applying this updater.'}
if(-not $BackupDirectory){$BackupDirectory=Join-Path $env:ProgramData ("ShadowServerCommand\Backups\"+$Role+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))}
if(Test-Path $BackupDirectory){throw 'Choose a new backup directory.'}
if(-not $PSCmdlet.ShouldProcess($name,'Verify package, stop only this SSC service, back up and replace binaries, verify startup; restore on failure')){return}
New-Item $BackupDirectory -ItemType Directory -Force | Out-Null
& icacls.exe $BackupDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Backup ACL failed.'}
$previousReady=$false
if($Role -eq 'Agent' -and $service.Status -eq 'Running'){$previousReady=(Get-SscAgentOperation).HttpsJournalReady}
$wasRunning=$service.Status -eq 'Running';$binaryBackup=Join-Path $BackupDirectory 'previous-binaries'
Stop-Service $name -ErrorAction Stop
(Get-Service $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
$backupComplete=$false;$copyStarted=$false
try {
    New-Item $binaryBackup -ItemType Directory | Out-Null
    Copy-Item (Join-Path $target '*') $binaryBackup -Recurse -Force
    # Even pre-manifest installations get a verified rollback package.
    & "$PSScriptRoot\Write-SscDeploymentManifest.ps1" -Role $Role -Directory $binaryBackup
    $null=Assert-Package $binaryBackup;$backupComplete=$true
    [pscustomobject]@{Service=$name;Identity=$installed.StartName;StartMode=$installed.StartMode;BackedUpUtc=[DateTime]::UtcNow.ToString('O')} |
        ConvertTo-Json | Set-Content (Join-Path $BackupDirectory 'update.json') -Encoding UTF8
    $copyStarted=$true
    foreach($file in $manifest.Files){
        $destination=Join-Path $target $file.Path
        New-Item ([IO.Path]::GetDirectoryName($destination)) -ItemType Directory -Force | Out-Null
        Copy-Item (Join-Path $source $file.Path) $destination -Force
    }
    Copy-Item (Join-Path $source 'deployment-manifest.json') $target -Force
    $null=Assert-Package $target
    $started=[DateTimeOffset]::UtcNow
    if($wasRunning){
        Start-Service $name;(Get-Service $name).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
        if($Role -eq 'Agent' -and -not $LegacyRollback){
            $ready=$false;$deadline=[DateTimeOffset]::UtcNow.AddSeconds(35)
            while([DateTimeOffset]::UtcNow -lt $deadline){
                $current=Get-CimInstance Win32_Service -Filter "Name='$name'"
                if($current.State -ne 'Running'){throw 'Agent exited during initialization; inspect startup journal.'}
                $journalDirectory=Join-Path $env:ProgramData 'ShadowServerCommand\Agent\logs'
                foreach($entry in @(Get-SscStartupRecords -Directory $journalDirectory)){
                    if($entry.Stage -eq 'HttpsReady' -and $entry.ProcessId -eq $current.ProcessId -and $entry.UtcTimestamp -ge $started){$ready=$true;break}
                }
                if($ready){break};Start-Sleep -Milliseconds 500
            }
            if(-not $ready){throw 'Agent did not reach HTTPS readiness. Inspect startup journal; SCM Running alone is insufficient.'}
        }
    }
    if($LegacyRollback){Write-Warning 'Legacy rollback: SCM Running verified; this original build has no new HTTPS-ready journal. Verify its listener/status manually.'}
    Write-Host "$name package hashes and startup verified. Backup: $binaryBackup"
    Write-Host 'Configuration, PFX files, encrypted secrets, history and SCM registration are unchanged. Run AM5 heartbeat verification for end-to-end authentication.'
} catch {
    $failure=$_
    if($backupComplete -and $copyStarted){
        Stop-Service $name -ErrorAction SilentlyContinue
        Copy-Item (Join-Path $binaryBackup '*') $target -Recurse -Force
        $null=Assert-Package $target
        $rollbackStarted=[DateTimeOffset]::UtcNow
        $rollback=[ordered]@{BinariesVerified=$true;ServiceRunning=$false;PreviousExpectedRunning=$wasRunning;OperationalVerified=$false;AuthenticatedHeartbeatVerified=$false}
        try {
            if($wasRunning){
                Start-Service $name;(Get-Service $name).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
                if($Role -eq 'Agent'){
                    $operation=Wait-SscAgentOperation -Since $rollbackStarted -RequireJournal $previousReady
                    $rollback.ServiceRunning=$operation.ServiceRunning;$rollback.ListenerReachable=$operation.ListenerReachable;$rollback.HttpsJournalReady=$operation.HttpsJournalReady
                    $rollback.OperationalVerified=$true
                    $rollback.VerificationLevel=if($previousReady){'Running + listener + fresh HTTPS readiness'}else{'Legacy running + listener only; TLS not verified'}
                }else{$rollback.ServiceRunning=$true;$rollback.OperationalVerified=$true;$rollback.VerificationLevel='SCM running only; remote monitoring not verified'}
            }else{$rollback.VerificationLevel='Previous service intentionally stopped';$rollback.OperationalVerified=$false}
        }catch{
            $rollback.ErrorType=$_.Exception.GetType().Name
            $rollback.ServiceRunning=(Get-Service $name -ErrorAction SilentlyContinue).Status -eq 'Running'
            Write-Warning 'Rollback files verified, but previous service operational recovery FAILED. Inspect rollback-result.json and startup logs.'
            throw
        }finally{$rollback | ConvertTo-Json | Set-Content (Join-Path $BackupDirectory 'rollback-result.json') -Encoding UTF8}
        Write-Warning ("Patch failed; previous binaries restored. Previous operational={0}; level={1}. AM5 authenticated heartbeats remain a separate check." -f $rollback.OperationalVerified,$rollback.VerificationLevel)
    } elseif($wasRunning){
        $recovery=[ordered]@{BinariesChanged=$false;ServiceRunning=$false;OperationalVerified=$false;AuthenticatedHeartbeatVerified=$false}
        try {
            $since=[DateTimeOffset]::UtcNow;Start-Service $name
            (Get-Service $name).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))
            if($Role -eq 'Agent'){$operation=Wait-SscAgentOperation -Since $since -RequireJournal $previousReady;$recovery.ServiceRunning=$operation.ServiceRunning;$recovery.ListenerReachable=$operation.ListenerReachable;$recovery.HttpsJournalReady=$operation.HttpsJournalReady}
            else{$recovery.ServiceRunning=$true}
            $recovery.OperationalVerified=$true
        }catch{$recovery.ErrorType=$_.Exception.GetType().Name;Write-Warning 'Original service operational recovery failed before binary replacement.';throw}
        finally{$recovery|ConvertTo-Json|Set-Content (Join-Path $BackupDirectory 'rollback-result.json') -Encoding UTF8}
    }
    throw $failure
}
