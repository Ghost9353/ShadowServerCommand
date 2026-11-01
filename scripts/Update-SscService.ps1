#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='Medium')]
param(
    [Parameter(Mandatory)][ValidateSet('Agent','Watchdog')][string]$Role,
    [Parameter(Mandatory)][string]$PublishDirectory,
    [string]$BackupDirectory
)
$ErrorActionPreference='Stop'
$name="SSC.$Role"
$target=Join-Path $env:ProgramFiles "ShadowServerCommand\$Role"
$source=(Resolve-Path $PublishDirectory).Path.TrimEnd('\')
if($source -eq $target.TrimEnd('\')){throw 'Source must be an extracted release or rollback directory, not the running installation.'}
if(-not(Test-Path (Join-Path $source "$name.exe")) -or -not(Test-Path (Join-Path $source "$name.dll"))){throw 'The source directory is not a complete Windows publish output.'}
$service=Get-Service $name -ErrorAction Stop
$installed=Get-CimInstance Win32_Service -Filter "Name='$name'"
$expected=Join-Path $target "$name.exe"
if($installed.PathName.Trim('"') -ne $expected){throw 'This script updates only the standard SSC installation path. Review the custom service path manually.'}
if(-not $BackupDirectory){$BackupDirectory=Join-Path $env:ProgramData ("ShadowServerCommand\Backups\"+$Role+'-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))}
if(Test-Path $BackupDirectory){throw 'Backup directory already exists; choose a new empty path.'}
if(-not $PSCmdlet.ShouldProcess($name,"Stop only $name, back up its binaries, copy the patch and restart it")){return}
New-Item $BackupDirectory -ItemType Directory -Force | Out-Null
& icacls.exe $BackupDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Backup ACL failed; installation untouched.'}
$wasRunning=$service.Status -eq 'Running'
$binaryBackup=Join-Path $BackupDirectory 'previous-binaries'
Stop-Service $name -ErrorAction Stop
(Get-Service $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30))
try {
    New-Item $binaryBackup -ItemType Directory | Out-Null
    Copy-Item (Join-Path $target '*') $binaryBackup -Recurse -Force
    [pscustomobject]@{Service=$name;OriginalDirectory=$target;WasRunning=$wasRunning;BackedUpUtc=[DateTime]::UtcNow.ToString('O')} |
        ConvertTo-Json | Set-Content (Join-Path $BackupDirectory 'update.json') -Encoding UTF8
    Copy-Item (Join-Path $source '*') $target -Recurse -Force
    # Existing directory ACLs, SCM identity/recovery actions, firewall rules and all ProgramData
    # configuration, PFX files, encrypted secrets, SQLite history/outbox and status remain untouched.
    if($wasRunning){Start-Service $name;(Get-Service $name).WaitForStatus('Running',[TimeSpan]::FromSeconds(30))}
    Write-Host "$name binaries updated. Rollback binaries: $binaryBackup"
    Write-Host 'Service Running alone does not prove TLS. On AM5, run Test-SscTlsRepair.ps1 and inspect TLS diagnostics.'
} catch {
    # Keep the service stopped rather than automatically restarting a potentially partial update.
    Stop-Service $name -ErrorAction SilentlyContinue
    Write-Warning "Update failed. Preserve diagnostics; restore from $binaryBackup using this script once that backup is complete."
    throw
}
