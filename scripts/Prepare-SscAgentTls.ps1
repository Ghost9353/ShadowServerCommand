#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess=$true)]
param([Parameter(Mandatory)][string]$AgentPublishDirectory)
$ErrorActionPreference='Stop'
# Never starts/stops the service. Existing config, PFX and DPAPI bytes remain unchanged.
$service=Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'"
if(-not $service -or $service.StartName -ne 'NT SERVICE\SSC.Agent'){throw 'Expected existing virtual Agent service registration.'}
if(-not $PSCmdlet.ShouldProcess('existing Agent certificate','Prepare persistent LocalMachine credential and read-only access to its exact key')){return}
$exe=Join-Path (Resolve-Path $AgentPublishDirectory).Path 'SSC.Agent.exe'
& $exe --prepare-tls
if($LASTEXITCODE -ne 0){throw 'Preparation failed. Existing Agent has not been stopped or updated. Inspect safe startup diagnostics.'}
