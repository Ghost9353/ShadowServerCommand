#Requires -Version 5.1
$ErrorActionPreference='Stop'
. "$PSScriptRoot\..\..\scripts\Ssc-StartupJournal.ps1"
. "$PSScriptRoot\..\..\scripts\Ssc-HeartbeatVerification.ps1"
function Assert-True([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
$start=[DateTimeOffset]::Parse('2026-11-07T12:00:00+00:00')
function Snapshot([int]$Second,[int]$Failures=0,[string]$Health='RUNNING'){
    [pscustomobject]@{WrittenAt=$start.AddSeconds($Second).ToString('O');ServiceHealth=$Health;
        State=[pscustomobject]@{Computer='Online';LastHeartbeat=$start.AddSeconds($Second).ToString('O');Failures=$Failures;Successes=3}}
}
$observed=@{Count=0;Last=$null}
Assert-True (-not (Update-SscHeartbeatVerification $observed $start (Snapshot -15))) 'Stale snapshot was accepted.'
Assert-True (-not (Update-SscHeartbeatVerification $observed $start (Snapshot 15))) 'Preexisting Successes=3 accepted only one fresh heartbeat.'
Assert-True (-not (Update-SscHeartbeatVerification $observed $start (Snapshot 15))) 'Duplicate heartbeat counted twice.'
Assert-True ($observed.Count -eq 1) 'Duplicate modified observation count.'
Assert-True (-not (Update-SscHeartbeatVerification $observed $start (Snapshot 30))) 'Only two fresh heartbeats accepted.'
Assert-True (Update-SscHeartbeatVerification $observed $start (Snapshot 45)) 'Three fresh heartbeats rejected.'
Assert-True (-not (Update-SscHeartbeatVerification $observed $start (Snapshot 60 1))) 'Failed heartbeat accepted.'
Assert-True ($observed.Count -eq 0) 'Failure did not reset observation count.'
foreach($second in @(75,90)){Assert-True (-not (Update-SscHeartbeatVerification $observed $start (Snapshot $second))) 'Recovery accepted too early.'}
Assert-True (Update-SscHeartbeatVerification $observed $start (Snapshot 105)) 'Three fresh recovery checks rejected.'
Assert-True (-not (Update-SscHeartbeatVerification $observed $start (Snapshot 120 0 'STARTING'))) 'Unhealthy service accepted.'
Assert-True ($observed.Count -eq 0) 'Unhealthy service did not reset observation count.'
Write-Host 'PASS: three distinct fresh AM5 heartbeats required; stale/duplicate/preexisting counts rejected; failures and service degradation reset acceptance.'
