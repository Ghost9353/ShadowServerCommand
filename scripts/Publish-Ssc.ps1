#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
foreach($role in @('Agent','Watchdog','Desktop')) {
    & dotnet publish "src/SSC.$role" -c Release -r win-x64 --self-contained false -o "artifacts/$($role.ToLowerInvariant())"
    if($LASTEXITCODE -ne 0){throw "Publish failed: $role"}
}
