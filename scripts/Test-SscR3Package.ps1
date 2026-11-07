#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference='Stop'
$root=(Resolve-Path $Directory).Path.TrimEnd('\')
$manifest=Get-Content (Join-Path $root 'release-manifest.json') -Raw | ConvertFrom-Json
if($manifest.Release -ne 'R3' -or $manifest.SourceCommit -notmatch '^[0-9a-f]{40}$' -or -not $manifest.Files){throw 'Invalid R3 release manifest.'}
$listed=@{}
foreach($file in $manifest.Files){
    $full=[IO.Path]::GetFullPath((Join-Path $root $file.Path))
    if(-not $full.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase) -or $listed.ContainsKey($full)){throw 'Unsafe or duplicate release manifest path.'}
    if(-not(Test-Path $full -PathType Leaf) -or (Get-FileHash $full -Algorithm SHA256).Hash -ne $file.Sha256){throw "R3 release integrity failed: $($file.Path)"}
    $listed[$full]=$true
}
foreach($file in @(Get-ChildItem $root -Recurse -File)){
    if($file.FullName -ne (Join-Path $root 'release-manifest.json') -and -not $listed.ContainsKey($file.FullName)){throw 'Unlisted file in R3 release.'}
}
foreach($required in @('agent/SSC.Agent.exe','agent/deployment-manifest.json','scripts/Prepare-SscAgentTls.ps1','scripts/Update-SscService.ps1','scripts/Test-SscTlsRepair.ps1','docs/R3-TLS-RECOVERY.md')){
    if(-not $listed.ContainsKey([IO.Path]::GetFullPath((Join-Path $root $required)))){throw "Required R3 component missing: $required"}
}
Write-Host ("PASS: R3 release integrity; source {0}; {1} files. This does not verify production TLS." -f $manifest.SourceCommit,$listed.Count)
