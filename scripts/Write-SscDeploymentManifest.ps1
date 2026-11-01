#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Agent','Watchdog')][string]$Role,[Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference='Stop'
$root=(Resolve-Path $Directory).Path.TrimEnd('\')
$files=@(Get-ChildItem $root -Recurse -File | Where-Object {$_.FullName -ne (Join-Path $root 'deployment-manifest.json')} | ForEach-Object {
    [pscustomobject]@{Path=$_.FullName.Substring($root.Length+1).Replace('\','/');Sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash}
})
[pscustomobject]@{Role=$Role;Files=$files} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'deployment-manifest.json') -Encoding UTF8
