#Requires -Version 5.1
# Shared by CI and deployment scripts. Preserve JSON timestamp precision across PS 5.1 / 7.x.
function ConvertFrom-SscJson {
    param([Parameter(Mandatory)][string]$Json)
    if((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')){
        return ConvertFrom-Json -InputObject $Json -DateKind String
    }
    return ConvertFrom-Json -InputObject $Json
}
function ConvertTo-SscUtcInstant {
    param([Parameter(Mandatory)]$Value)
    if($Value -is [DateTimeOffset]){return $Value.ToUniversalTime()}
    if($Value -is [DateTime]){
        if($Value.Kind -eq [DateTimeKind]::Unspecified){throw 'Timestamp lost its timezone; refusing an ambiguous freshness check.'}
        return ([DateTimeOffset]::new($Value)).ToUniversalTime()
    }
    return [DateTimeOffset]::Parse([string]$Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None).ToUniversalTime()
}
function Get-SscStartupRecords {
    param([Parameter(Mandatory)][string]$Directory,[ValidateRange(1,10000)][int]$Tail=200)
    if(-not(Test-Path -LiteralPath $Directory)){return}
    foreach($file in @(Get-ChildItem -LiteralPath $Directory -Filter 'startup-*.jsonl' -File | Sort-Object Name)){
        $stream=$null;$reader=$null
        try {
            $stream=[IO.FileStream]::new($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $reader=[IO.StreamReader]::new($stream)
            $lines=[Collections.Generic.Queue[string]]::new()
            while(-not $reader.EndOfStream){$lines.Enqueue($reader.ReadLine());if($lines.Count -gt $Tail){$null=$lines.Dequeue()}}
        } catch [IO.FileNotFoundException] {continue} catch [IO.DirectoryNotFoundException] {continue}
        finally {if($reader){$reader.Dispose()}elseif($stream){$stream.Dispose()}}
        $pending=@($lines.ToArray())
        for($index=0;$index -lt $pending.Count;$index++){
            $line=$pending[$index]
            if([string]::IsNullOrWhiteSpace($line)){continue}
            try {$entry=ConvertFrom-SscJson $line}
            catch {
                if($index -eq $pending.Count-1 -and -not $line.TrimEnd().EndsWith('}')){Write-Verbose "Deferring partial EOF record: $($file.Name)";continue}
                throw # A complete malformed record is a real error, never silently ignored.
            }
            if(-not $entry.Stage -or -not $entry.ProcessId -or -not $entry.TimestampUtc -or -not $entry.Build){throw "Malformed startup record in $($file.Name)."}
            $utc=ConvertTo-SscUtcInstant $entry.TimestampUtc
            $entry | Add-Member -NotePropertyName UtcTimestamp -NotePropertyValue $utc
            $entry | Add-Member -NotePropertyName SourceFile -NotePropertyValue $file.Name
            $entry | Add-Member -NotePropertyName ProcessKey -NotePropertyValue ("{0}|{1}|{2}" -f $entry.ProcessId,$entry.Build,$utc.ToString('O'))
            $entry
        }
    }
}
function Find-SscFreshStartupFailure {
    param([object[]]$Records,[string[]]$KnownProcessKeys)
    $starts=@($Records | Where-Object {$_.Stage -eq 'ProcessEntry' -and $KnownProcessKeys -notcontains $_.ProcessKey})
    if($starts.Count -gt 1){throw 'More than one fresh Agent process appeared during the single-start failure test.'}
    if(-not $starts.Count){return}
    $start=$starts[0]
    $Records | Where-Object { $_.Stage -eq 'StartupFailed' -and $_.ProcessId -eq $start.ProcessId -and
        $_.Build -eq $start.Build -and $_.ElapsedMs -ge $start.ElapsedMs -and $_.UtcTimestamp -ge $start.UtcTimestamp }
}
