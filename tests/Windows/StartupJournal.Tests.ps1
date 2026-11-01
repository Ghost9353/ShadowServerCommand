#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
. "$PSScriptRoot\..\..\scripts\Ssc-StartupJournal.ps1"
function Assert-True([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
function New-Record([string]$Stage,[int]$ProcessNumber,[string]$Time,[int]$Elapsed){
    return [pscustomobject]@{Stage=$Stage;ProcessId=$ProcessNumber;TimestampUtc=$Time;Build='fixture-build';ElapsedMs=$Elapsed;ErrorType='CryptographicException';HResult='0x80070056'}
}
$raw='2026-11-01T21:26:19.4440345+00:00'
$expected=[DateTimeOffset]::Parse($raw,[Globalization.CultureInfo]::InvariantCulture)
$threshold=[DateTimeOffset]::Parse('2026-11-01T21:26:19.1000000+00:00')
$typed=$expected.UtcDateTime
$legacy=[DateTimeOffset]::Parse($typed)
Write-Host ("Precision regression: runtime={0}; typedInput={1}; originalPredicate={2}; legacyUtc={3}; expectedUtc={4}" -f
    $PSVersionTable.PSVersion,$typed.GetType().FullName,($legacy -ge $threshold),$legacy.ToString('O'),$expected.ToString('O'))
foreach($value in @($raw,$expected,$typed,$typed.ToLocalTime())){
    $normalized=ConvertTo-SscUtcInstant $value
    Assert-True ($normalized.UtcDateTime.Ticks -eq $expected.UtcDateTime.Ticks) 'Timestamp precision/instant changed.'
    Assert-True ($normalized -ge $threshold) 'Same-second fresh timestamp was rejected.'
}
$json=ConvertFrom-SscJson ('{"TimestampUtc":"'+$raw+'"}')
Assert-True ((ConvertTo-SscUtcInstant $json.TimestampUtc).UtcDateTime.Ticks -eq $expected.UtcDateTime.Ticks) 'JSON timestamp lost precision.'
$culture=[Threading.Thread]::CurrentThread.CurrentCulture
try {
    [Threading.Thread]::CurrentThread.CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo('fr-FR')
    Assert-True ((ConvertTo-SscUtcInstant $raw).UtcDateTime.Ticks -eq $expected.UtcDateTime.Ticks) 'Timestamp parser depends on current culture.'
} finally {[Threading.Thread]::CurrentThread.CurrentCulture=$culture}
$rejected=$false
try {ConvertTo-SscUtcInstant ([DateTime]::SpecifyKind($typed,[DateTimeKind]::Unspecified)) | Out-Null}catch{$rejected=$true}
Assert-True $rejected 'Ambiguous unzoned DateTime must not pass a freshness check.'
$directory=Join-Path ([IO.Path]::GetTempPath()) ('ssc-journal-tests-'+[Guid]::NewGuid())
New-Item $directory -ItemType Directory | Out-Null
try {
    $history=Join-Path $directory 'startup-20261101-000000000.jsonl'
    $active=Join-Path $directory 'startup-20261101.jsonl'
    $oldStart=New-Record 'ProcessEntry' 6488 '2026-11-01T21:25:00.0000000+00:00' 0
    $oldFailure=New-Record 'StartupFailed' 6488 '2026-11-01T21:25:00.2000000+00:00' 200
    [IO.File]::WriteAllLines($history,@(($oldStart|ConvertTo-Json -Compress),($oldFailure|ConvertTo-Json -Compress)))
    $known=@(Get-SscStartupRecords $directory | Where-Object Stage -eq 'ProcessEntry' | ForEach-Object ProcessKey)
    $start=New-Record 'ProcessEntry' 6488 '2026-11-01T21:26:19.2000000+00:00' 0
    $failed=New-Record 'StartupFailed' 6488 $raw 244
    $content=($start|ConvertTo-Json -Compress)+"`n"+($failed|ConvertTo-Json -Compress)+"`n"+'{"Stage":'
    [IO.File]::WriteAllText($active,$content)
    $records=@(Get-SscStartupRecords $directory)
    $found=@(Find-SscFreshStartupFailure -Records $records -KnownProcessKeys $known)
    Assert-True ($found.Count -eq 1) 'Fresh failure missing or stale/reused-PID failure accepted.'
    Assert-True ($found[0].UtcTimestamp.UtcDateTime.Ticks -eq $expected.UtcDateTime.Ticks) 'Wrong process attempt selected.'
    $rotated=Join-Path $directory 'startup-20261101-010000000.jsonl'
    Move-Item $active $rotated;[IO.File]::WriteAllText($active,'')
    $found=@(Find-SscFreshStartupFailure -Records @(Get-SscStartupRecords $directory) -KnownProcessKeys $known)
    Assert-True ($found.Count -eq 1 -and $found[0].SourceFile -eq [IO.Path]::GetFileName($rotated)) 'Rotation hid the failure.'
    $missing=@(Find-SscFreshStartupFailure -Records @($records|Where-Object Stage -ne 'StartupFailed') -KnownProcessKeys $known)
    Assert-True ($missing.Count -eq 0) 'Missing diagnostics incorrectly accepted.'
    [IO.File]::WriteAllText($active,'{"Stage": invalid}')
    $malformedRejected=$false
    try {Get-SscStartupRecords $directory | Out-Null}catch{$malformedRejected=$true}
    Assert-True $malformedRejected 'Complete malformed journal record was silently ignored.'
    Write-Host 'PASS: timestamp types/culture/subsecond comparison, stale/reused PID rejection, rotation, partial EOF, missing-stage rejection, malformed-record rejection.'
} finally {Remove-Item $directory -Recurse -Force}
