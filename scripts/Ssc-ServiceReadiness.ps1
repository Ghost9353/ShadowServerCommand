#Requires -Version 5.1
function Get-SscAgentOperation {
    param([DateTimeOffset]$Since=[DateTimeOffset]::MinValue)
    $service=Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'"
    $running=$service -and $service.State -eq 'Running';$listening=$false;$ready=$false
    if($running){
        $process=Get-CimInstance Win32_Process -Filter ("ProcessId="+$service.ProcessId)
        if($process.CreationDate){$created=ConvertTo-SscUtcInstant $process.CreationDate;if($created -gt $Since){$Since=$created}}
        $config=ConvertFrom-SscJson (Get-Content (Join-Path $env:ProgramData 'ShadowServerCommand\Agent\service.json') -Raw)
        $client=[Net.Sockets.TcpClient]::new()
        try{$task=$client.ConnectAsync([string]$config.Heartbeat.BindAddress,[int]$config.Heartbeat.Port);if($task.Wait(1000)){$task.GetAwaiter().GetResult();$listening=$true}}catch{}finally{$client.Dispose()}
        foreach($entry in @(Get-SscStartupRecords (Join-Path $env:ProgramData 'ShadowServerCommand\Agent\logs'))){
            if($entry.ProcessId -eq $service.ProcessId -and $entry.Stage -eq 'HttpsReady' -and $entry.UtcTimestamp -ge $Since){$ready=$true;break}
        }
    }
    [pscustomobject]@{ServiceRunning=[bool]$running;ProcessId=$service.ProcessId;ListenerReachable=$listening;HttpsJournalReady=$ready;AuthenticatedHeartbeatVerified=$false}
}
function Wait-SscAgentOperation {
    param([DateTimeOffset]$Since,[bool]$RequireJournal,[int]$TimeoutSeconds=35)
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $result=Get-SscAgentOperation -Since $Since
        if($result.ServiceRunning -and $result.ListenerReachable -and (-not $RequireJournal -or $result.HttpsJournalReady)){return $result}
        Start-Sleep -Milliseconds 500
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'Agent operational verification failed: running/listener/readiness conditions were not met. End-to-end TLS requires AM5 verification.'
}
