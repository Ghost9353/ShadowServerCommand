#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('agent-pfx','watchdog-pfx','discord-webhook')][string]$Name,
      [Parameter(Mandatory)][ValidateSet('Agent','Watchdog')][string]$Role)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Security
$directory=Join-Path $env:ProgramData 'ShadowServerCommand\Secrets'
New-Item $directory -ItemType Directory -Force | Out-Null
$identity="NT SERVICE\SSC.$Role"
if (-not (Get-Service "SSC.$Role" -ErrorAction SilentlyContinue)) { throw 'Install the service first, without starting it.' }
# DPAPI LocalMachine is secure only with restrictive ACLs on the encrypted files.
& icacls.exe $directory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${identity}:(RX)" | Out-Null
if($LASTEXITCODE -ne 0){throw 'Secret directory ACL failed'}
$secret=Read-Host "Enter $Name (hidden; never saved as plaintext)" -AsSecureString
$pointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
try {
    $plaintext=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    if($Name -eq 'discord-webhook' -and $plaintext -notmatch '^https://discord\.com/api/webhooks/[^/?#]+/[^/?#]+$'){throw 'Expected an HTTPS discord.com webhook without query parameters.'}
    $bytes=[Text.Encoding]::UTF8.GetBytes($plaintext)
    try {
        $encrypted=[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine)
        $path=Join-Path $directory ($Name+'.bin');[IO.File]::WriteAllBytes($path,$encrypted)
        & icacls.exe $path /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' "${identity}:R" | Out-Null
        if($LASTEXITCODE -ne 0){throw 'Secret file ACL failed'}
    } finally { [Array]::Clear($bytes,0,$bytes.Length);$plaintext=$null }
} finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer);$secret.Dispose() }
Write-Host 'Secret provisioned. No secret value was printed. Restart the service after configuration changes.'
