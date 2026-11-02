#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess=$true)]
param([Parameter(Mandatory)][string]$AgentPublishDirectory,[Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedServerSha256)
$ErrorActionPreference='Stop'
# Never starts/stops the service. Existing config, PFX and DPAPI bytes remain unchanged.
$service=Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'"
if(-not $service -or $service.StartName -ne 'NT SERVICE\SSC.Agent'){throw 'Expected existing virtual Agent service registration.'}
if(-not $PSCmdlet.ShouldProcess('existing Agent certificate','Prepare persistent LocalMachine credential and read-only access to its exact key')){return}
$directory=(Resolve-Path $AgentPublishDirectory).Path
$manifest=Get-Content (Join-Path $directory 'deployment-manifest.json') -Raw | ConvertFrom-Json
if($manifest.Role -ne 'Agent' -or -not $manifest.Files){throw 'Invalid Agent deployment manifest.'}
foreach($file in $manifest.Files){
    $full=[IO.Path]::GetFullPath((Join-Path $directory $file.Path))
    if(-not $full.StartsWith($directory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash $full -Algorithm SHA256).Hash -ne $file.Sha256){throw 'Preparation package integrity failed; service untouched.'}
}
$exe=Join-Path $directory 'SSC.Agent.exe' 
& $exe --prepare-tls "--expected-server-pin=$ExpectedServerSha256"
if($LASTEXITCODE -ne 0){throw 'Preparation failed. Existing Agent has not been stopped or updated. Inspect safe startup diagnostics.'}
