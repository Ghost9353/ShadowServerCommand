#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,
      [ipaddress]$AgentAddress="10.10.10.2",[ipaddress]$WatchdogAddress="10.10.10.1")
$ErrorActionPreference='Stop'
if(Test-Path $OutputDirectory){throw 'Choose a new empty staging directory; existing files are not overwritten.'}
New-Item $OutputDirectory -ItemType Directory | Out-Null
& icacls.exe $OutputDirectory /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if($LASTEXITCODE -ne 0){throw 'Staging ACL failed'}
foreach($role in @('Agent','Watchdog')) {
    $eku=if($role -eq 'Agent'){'1.3.6.1.5.5.7.3.1'}else{'1.3.6.1.5.5.7.3.2'}
    $address=if($role -eq 'Agent'){$AgentAddress.IPAddressToString}else{$WatchdogAddress.IPAddressToString}
    $certificate=New-SelfSignedCertificate -Subject "CN=SSC.$role" -CertStoreLocation 'Cert:\LocalMachine\My' -KeyAlgorithm RSA -KeyLength 3072 -Provider "Microsoft Software Key Storage Provider" -HashAlgorithm SHA256 -KeyExportPolicy Exportable -KeyUsage DigitalSignature,KeyEncipherment -Type Custom -TextExtension @("2.5.29.37={text}$eku", "2.5.29.17={text}IPAddress=$address&DNS=SSC.$role") -NotAfter (Get-Date).AddYears(1)
    $password=Read-Host "Choose $role PFX password; provision the same password on its destination PC" -AsSecureString
    try {
        Export-PfxCertificate -Cert $certificate -FilePath (Join-Path $OutputDirectory "$role.pfx") -Password $password | Out-Null
        $sha=[Security.Cryptography.SHA256]::Create()
        try { $pin=([BitConverter]::ToString($sha.ComputeHash($certificate.RawData))).Replace('-','') }finally{$sha.Dispose()}
        Set-Content -Path (Join-Path $OutputDirectory "$role.sha256.txt") -Value $pin -Encoding ASCII
        Write-Host "$role SHA256 pin: $pin (expires $($certificate.NotAfter))"
    } finally { $password.Dispose();Remove-Item "Cert:\LocalMachine\My\$($certificate.Thumbprint)" -DeleteKey }
}
Write-Host 'Transfer only Agent.pfx to AM4 and Watchdog.pfx to AM5 using a trusted channel. Both public pin files are safe to share. Protect and eventually remove the staging PFX files.'
