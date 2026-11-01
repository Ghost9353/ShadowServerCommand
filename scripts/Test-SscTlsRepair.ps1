#Requires -Version 5.1
[CmdletBinding()]
param([ValidateRange(15,600)][int]$TimeoutSeconds=120)
$ErrorActionPreference='Stop'
. "$PSScriptRoot\Ssc-StartupJournal.ps1"
$start=[DateTimeOffset]::UtcNow
$deadline=$start.AddSeconds($TimeoutSeconds)
$path=Join-Path $env:ProgramData 'ShadowServerCommand\Status\watchdog.json'
while([DateTimeOffset]::UtcNow -lt $deadline){
    $service=Get-Service SSC.Watchdog -ErrorAction Stop
    if($service.Status -ne 'Running'){throw 'SSC.Watchdog is not running; check its startup/TLS credential logs.'}
    if(Test-Path $path){
        try {
            $snapshot=ConvertFrom-SscJson (Get-Content $path -Raw)
            $written=(ConvertTo-SscUtcInstant $snapshot.WrittenAt)
            $heartbeat=if($snapshot.State.LastHeartbeat){(ConvertTo-SscUtcInstant $snapshot.State.LastHeartbeat)}else{$null}
            $online=($snapshot.State.Computer -eq 1 -or $snapshot.State.Computer -eq 'Online')
            if($snapshot.ServiceHealth -eq 'RUNNING' -and $written -ge $start -and $heartbeat -and
               $heartbeat -ge $start -and $online -and $snapshot.State.Failures -eq 0 -and $snapshot.State.Successes -ge 3){
                [pscustomobject]@{AuthenticatedHeartbeat=$true;Computer='Online';LastHeartbeat=$heartbeat;
                    HeartbeatLatencyMs=$snapshot.State.HeartbeatLatencyMs;ConsecutiveFailures=$snapshot.State.Failures;
                    LastEvidence=$snapshot.State.LastEvidence;DeliveryStatus=$snapshot.DeliveryStatus;LastDelivered=$snapshot.LastDelivered} | Format-List
                Write-Host 'PASS: fresh authenticated heartbeat and three consecutive successful checks. Verify recovery and Discord delivery using the runbook.'
                exit 0
            }
        } catch [System.IO.IOException] { } # An atomic replacement may race a read; retry.
    }
    Start-Sleep -Seconds 2
}
throw 'Authenticated heartbeat/recovery not confirmed within the timeout. Inspect Agent TLS handshake and Watchdog validation logs; do not treat TCP reachability as authentication.'
