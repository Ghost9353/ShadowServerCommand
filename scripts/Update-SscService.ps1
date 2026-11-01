#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='Medium')]
param([Parameter(Mandatory)][ValidateSet('Agent','Watchdog')][string]$Role,
      [Parameter(Mandatory)][string]$PublishDirectory,[string]$BackupDirectory)
$ErrorActionPreference='Stop'
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
$manifest=Assert-Package $source # Before stopping anything.
$service=Get-Service $name -ErrorAction Stop
$installed=Get-CimInstance Win32_Service -Filter "Name='$name'"
if($installed.PathName.Trim('"') -ne (Join-Path $target "$name.exe")){throw 'Nonstandard executable path; inspect registration before applying this updater.'}
if(-not $BackupDirectory){$BackupDirectory=Join-Path $env:ProgramData ("ShadowServerCommand\Backups\"+$Role+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))}
if(Test-Path $BackupDirectory){throw 'Choose a new backup directory.'}
if(-not $PSCmdlet.ShouldProcess($name,'Verify package, stop only this SSC service, back up and replace binaries, verify startup; restore on failure')){return}
New-Item $BackupDirectory -ItemType Directory -Force | Out-Null
& icacls.exe $BackupDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Backup ACL failed.'}
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
        if($Role -eq 'Agent'){
            $ready=$false;$deadline=[DateTimeOffset]::UtcNow.AddSeconds(35)
            while([DateTimeOffset]::UtcNow -lt $deadline){
                $current=Get-CimInstance Win32_Service -Filter "Name='$name'"
                if($current.State -ne 'Running'){throw 'Agent exited during initialization; inspect startup journal.'}
                $journal=Join-Path $env:ProgramData ('ShadowServerCommand\Agent\logs\startup-'+[DateTime]::UtcNow.ToString('yyyyMMdd')+'.jsonl')
                if(Test-Path $journal){
                    foreach($line in (Get-Content $journal -Tail 50)){
                        $entry=$line | ConvertFrom-Json
                        if($entry.Stage -eq 'HttpsReady' -and $entry.ProcessId -eq $current.ProcessId -and [DateTimeOffset]::Parse($entry.TimestampUtc) -ge $started){$ready=$true;break}
                    }
                }
                if($ready){break};Start-Sleep -Milliseconds 500
            }
            if(-not $ready){throw 'Agent did not reach HTTPS readiness. Inspect startup journal; SCM Running alone is insufficient.'}
        }
    }
    Write-Host "$name package hashes and startup verified. Backup: $binaryBackup"
    Write-Host 'Configuration, PFX files, encrypted secrets, history and SCM registration are unchanged. Run AM5 heartbeat verification for end-to-end authentication.'
} catch {
    $failure=$_
    if($backupComplete -and $copyStarted){
        Stop-Service $name -ErrorAction SilentlyContinue
        Copy-Item (Join-Path $binaryBackup '*') $target -Recurse -Force
        $null=Assert-Package $target
        if($wasRunning){try{Start-Service $name}catch{Write-Warning 'Previous binaries restored; manual service start still required.'}}
        Write-Warning 'Patch failed; previous binaries have been restored. Startup logs remain in ProgramData.'
    } elseif($wasRunning){try{Start-Service $name}catch{Write-Warning 'Service remains stopped; start the original installation manually.'}}
    throw $failure
}
