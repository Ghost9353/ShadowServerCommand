#Requires -Version 5.1
# Sourced by the read-only AM5 verifier and its deterministic regression tests.
function Update-SscHeartbeatVerification {
    param([hashtable]$Observation,[DateTimeOffset]$StartedAt,[object]$Snapshot)
    $written=ConvertTo-SscUtcInstant $Snapshot.WrittenAt
    if($written -lt $StartedAt){return $false}
    $online=($Snapshot.State.Computer -eq 1 -or $Snapshot.State.Computer -eq 'Online')
    if($Snapshot.ServiceHealth -ne 'RUNNING' -or -not $online -or $Snapshot.State.Failures -ne 0 -or -not $Snapshot.State.LastHeartbeat){
        $Observation.Count=0;$Observation.Last=$null;return $false
    }
    $heartbeat=ConvertTo-SscUtcInstant $Snapshot.State.LastHeartbeat
    if($heartbeat -lt $StartedAt -or ($Observation.Last -and $heartbeat -le $Observation.Last)){return $false}
    $Observation.Last=$heartbeat;$Observation.Count++
    return ($Observation.Count -ge 3 -and $Snapshot.State.Successes -ge 3)
}
