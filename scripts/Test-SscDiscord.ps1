#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
# A separate command process must not share the service's outbox while it is running.
$service=Get-Service SSC.Watchdog -ErrorAction Stop
if($service.Status -ne 'Stopped'){throw 'Stop-Service SSC.Watchdog first. This only pauses monitoring; Minecraft is not touched. Restart it immediately after the test.'}
& (Join-Path $env:ProgramFiles 'ShadowServerCommand\Watchdog\SSC.Watchdog.exe') --test-alert
if($LASTEXITCODE -ne 0){throw 'Delivery not confirmed. The alert remains queued. Check provisioning/network/Discord; restart the service.'}
Write-Host 'Verify the OPERATOR TEST message in Discord. Start-Service SSC.Watchdog to resume monitoring.'
