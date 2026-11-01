#Requires -Version 5.1
[CmdletBinding()]
param()
Get-Service SSC.Agent,SSC.Watchdog -ErrorAction SilentlyContinue | Select-Object Name,Status,StartType
$path=Join-Path $env:ProgramData 'ShadowServerCommand\Status\watchdog.json'
if(Test-Path $path){Get-Content $path -Raw | ConvertFrom-Json | Format-List WrittenAt,ServiceHealth,PendingAlerts,HeldAlerts,LastDelivered,DeliveryStatus,State}else{Write-Host 'No local AM5 watchdog status snapshot found.'}
Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName='SSC.Agent','SSC.Watchdog'} -MaxEvents 10 -ErrorAction SilentlyContinue | Select-Object TimeCreated,LevelDisplayName,Message
