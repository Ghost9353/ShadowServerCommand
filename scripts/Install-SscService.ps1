#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Agent','Watchdog')][string]$Role,
      [Parameter(Mandatory)][string]$PublishDirectory,
      [Parameter(Mandatory)][string]$ConfigurationFile,
      [Parameter(Mandatory)][string]$CertificateFile,
      [string]$TrustedWatchdogAddress)
$ErrorActionPreference='Stop'
function Check-Native { if($LASTEXITCODE -ne 0){throw "Native operation failed ($LASTEXITCODE)"} }
$name="SSC.$Role";$identity="NT SERVICE\$name"
if(Get-Service $name -ErrorAction SilentlyContinue){throw 'Service exists. Stop and uninstall it first; data and secrets are preserved.'}
$executable=Join-Path $PublishDirectory "$name.exe"
if(-not(Test-Path $executable)){throw 'Publish the Windows x64 service before installing.'}
$config=Get-Content $ConfigurationFile -Raw | ConvertFrom-Json
if($Role -eq 'Agent') {
    if(-not $TrustedWatchdogAddress -or $TrustedWatchdogAddress -notmatch '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)'){throw 'Supply the single trusted AM5 private IPv4 address for the firewall rule.'}
    if(-not $config.Heartbeat.Enabled -or $config.Heartbeat.BindAddress -eq '0.0.0.0'){throw 'Configure and enable an explicit private AM4 address.'}
}else{if(-not $config.Watchdog.Enabled){throw 'Enable the configured Watchdog.'}}
$binaryDirectory=Join-Path $env:ProgramFiles "ShadowServerCommand\$Role"
$dataDirectory=Join-Path $env:ProgramData "ShadowServerCommand\$Role"
$rootDirectory=Join-Path $env:ProgramData 'ShadowServerCommand'
New-Item $binaryDirectory -ItemType Directory -Force | Out-Null
New-Item $dataDirectory -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $PublishDirectory '*') $binaryDirectory -Recurse -Force
$binary=Join-Path $binaryDirectory "$name.exe"
& sc.exe create $name binPath= "`"$binary`"" start= auto obj= $identity DisplayName= "Shadow Server Command $Role" | Out-Null
Check-Native
& sc.exe description $name 'Independent read-only monitoring. No Minecraft start/stop or desktop dependency.' | Out-Null
Check-Native
& sc.exe failure $name reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
Check-Native
& sc.exe failureflag $name 1 | Out-Null
Check-Native
# Virtual service accounts have no interactive login or administrator rights.
& icacls.exe $rootDirectory /grant "${identity}:(RX)" | Out-Null
Check-Native
& icacls.exe $binaryDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${identity}:(OI)(CI)RX" | Out-Null
Check-Native
& icacls.exe $dataDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${identity}:(OI)(CI)M" | Out-Null
Check-Native
$certificateDestination=Join-Path $dataDirectory 'identity.pfx';Copy-Item $CertificateFile $certificateDestination -Force
if($Role -eq 'Agent'){$config.Heartbeat.CertificatePath=$certificateDestination}else{$config.Watchdog.ClientCertificatePath=$certificateDestination}
$config | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $dataDirectory 'service.json') -Encoding UTF8
if(-not [Diagnostics.EventLog]::SourceExists($name)){New-EventLog -LogName Application -Source $name}
if($Role -eq 'Watchdog') {
    $status=Join-Path $rootDirectory 'Status';New-Item $status -ItemType Directory -Force | Out-Null
    & icacls.exe $status /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' "${identity}:(OI)(CI)M" | Out-Null
    Check-Native
}else{
    New-NetFirewallRule -DisplayName 'SSC.Agent trusted AM5 heartbeat' -Direction Inbound -Action Allow -Protocol TCP -LocalAddress $config.Heartbeat.BindAddress -LocalPort $config.Heartbeat.Port -RemoteAddress $TrustedWatchdogAddress -Profile Private -Program $binary | Out-Null
}
Write-Host "$name installed, not started. Provision DPAPI secrets with Set-SscSecret.ps1, then Start-Service $name. No Minecraft files were changed."
