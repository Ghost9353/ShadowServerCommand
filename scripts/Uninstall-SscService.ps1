#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess=$true,ConfirmImpact='High')]
param([Parameter(Mandatory)][ValidateSet('Agent','Watchdog')][string]$Role)
$ErrorActionPreference='Stop';$name="SSC.$Role"
if($PSCmdlet.ShouldProcess($name,'Stop monitoring and remove the Windows service; preserve all binaries, history, configuration and secrets')) {
    if(Get-Service $name -ErrorAction SilentlyContinue){Stop-Service $name -ErrorAction Stop;& sc.exe delete $name;if($LASTEXITCODE -ne 0){throw 'Service removal failed'}}
    if($Role -eq 'Agent'){Get-NetFirewallRule -DisplayName 'SSC.Agent trusted AM5 heartbeat' -ErrorAction SilentlyContinue | Remove-NetFirewallRule}
    Write-Host 'Service uninstalled. Data, binaries, secrets and Event Log source preserved for audit/reinstallation.'
}
