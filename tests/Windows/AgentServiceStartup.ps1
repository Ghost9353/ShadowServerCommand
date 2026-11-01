#Requires -Version 7.0
#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference='Stop'
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows'){throw 'Disposable Windows GitHub runner only. Never run this fixture on AM4 or AM5.'}
if(Get-Service SSC.Agent -ErrorAction SilentlyContinue){throw 'SSC.Agent already exists; refusing to touch an existing installation.'}
$root=Join-Path $env:ProgramData 'ShadowServerCommand'
$target=Join-Path $env:ProgramFiles 'ShadowServerCommand\Agent'
if((Test-Path $root) -or (Test-Path $target)){throw 'Existing SSC data/binaries detected; fixture refuses to overwrite them.'}
$began=Get-Date
$artifacts=Join-Path $env:GITHUB_WORKSPACE 'TestResults\service-startup'
New-Item $artifacts -ItemType Directory -Force | Out-Null
$agentCert=$null;$clientCert=$null
try {
    New-Item "$root\Agent" -ItemType Directory -Force | Out-Null
    New-Item "$root\Secrets" -ItemType Directory -Force | Out-Null
    New-Item $target -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $PublishDirectory '*') $target -Recurse -Force
    $agentCert=New-SelfSignedCertificate -Subject 'CN=SSC.ServiceFixture' -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy Exportable -KeyUsage DigitalSignature,KeyEncipherment -Type Custom -TextExtension '2.5.29.37={text}1.3.6.1.5.5.7.3.1' -NotAfter (Get-Date).AddDays(1)
    $clientCert=New-SelfSignedCertificate -Subject 'CN=SSC.ClientFixture' -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy Exportable -KeyUsage DigitalSignature,KeyEncipherment -Type Custom -TextExtension '2.5.29.37={text}1.3.6.1.5.5.7.3.2' -NotAfter (Get-Date).AddDays(1)
    $password=[Guid]::NewGuid().ToString('N')
    $secure=ConvertTo-SecureString $password -AsPlainText -Force
    Export-PfxCertificate -Cert $agentCert -FilePath "$root\Agent\identity.pfx" -Password $secure | Out-Null
    $bytes=[Text.Encoding]::UTF8.GetBytes($password)
    [IO.File]::WriteAllBytes("$root\Secrets\agent-pfx.bin",[Security.Cryptography.ProtectedData]::Protect($bytes,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine))
    [Array]::Clear($bytes,0,$bytes.Length);$password=$null;$secure.Dispose()
    $serverPin=$agentCert.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
    $clientPin=$clientCert.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)
    @{Heartbeat=@{Enabled=$true;BindAddress='127.0.0.1';Port=27443;CertificatePath="$root\Agent\identity.pfx";CertificatePasswordSecret='agent-pfx';TrustedClientSha256=$clientPin}} |
        ConvertTo-Json | Set-Content "$root\Agent\service.json" -Encoding utf8
    # Remove the generator's store entry; the production service must load its own PFX key.
    Remove-Item "Cert:\LocalMachine\My\$($agentCert.Thumbprint)" -DeleteKey
    & sc.exe create SSC.Agent binPath= "`"$target\SSC.Agent.exe`"" obj= 'NT SERVICE\SSC.Agent' start= auto | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Fixture service creation failed.'}
    if(-not [Diagnostics.EventLog]::SourceExists('SSC.Agent')){New-EventLog -LogName Application -Source SSC.Agent}
    & icacls.exe $root /grant 'NT SERVICE\SSC.Agent:(RX)' | Out-Null
    & icacls.exe "$root\Agent" /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' 'NT SERVICE\SSC.Agent:(OI)(CI)M' | Out-Null
    & icacls.exe "$root\Secrets" /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' 'NT SERVICE\SSC.Agent:(OI)(CI)RX' | Out-Null
    & icacls.exe $target /grant 'NT SERVICE\SSC.Agent:(OI)(CI)RX' | Out-Null
    $timer=[Diagnostics.Stopwatch]::StartNew()
    & sc.exe start SSC.Agent | Out-File "$artifacts\sc-start.txt"
    if($LASTEXITCODE -ne 0){throw "SCM rejected startup after $($timer.Elapsed.TotalSeconds) seconds."}
    (Get-Service SSC.Agent).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
    Add-Type -TypeDefinition @'
using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
public static class SscFixtureHttp {
 public static async Task<string> Get(X509Certificate2 client,string serverPin) {
  using(var handler=new HttpClientHandler { UseProxy=false,AllowAutoRedirect=false }) {
   handler.ClientCertificates.Add(client);
   handler.ServerCertificateCustomValidationCallback=(r,c,chain,e)=>c!=null && c.GetCertHashString(HashAlgorithmName.SHA256)==serverPin && c.NotAfter.ToUniversalTime()>DateTime.UtcNow;
   using(var http=new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(4) }) return await http.GetStringAsync("https://127.0.0.1:27443/heartbeat");
  }
 }
}
'@
    $deadline=(Get-Date).AddSeconds(45);$heartbeat=$null
    while((Get-Date) -lt $deadline){
        try {$heartbeat=[SscFixtureHttp]::Get($clientCert,$serverPin).GetAwaiter().GetResult() | ConvertFrom-Json;break}catch{Start-Sleep -Seconds 1}
    }
    if(-not $heartbeat -or -not $heartbeat.agentHealthy){throw 'Service Running but authenticated heartbeat did not become healthy.'}
    [pscustomobject]@{Service='SSC.Agent';Identity=(Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'").StartName;StartupSeconds=$timer.Elapsed.TotalSeconds;AuthenticatedHeartbeat=$true;ObservedAt=$heartbeat.observedAt} |
        ConvertTo-Json | Set-Content "$artifacts\result.json"
    Get-Content "$artifacts\result.json"
} finally {
    if(Test-Path "$root\Agent\logs"){Copy-Item "$root\Agent\logs" "$artifacts\logs" -Recurse -Force}
    Get-WinEvent -FilterHashtable @{LogName='System';Id=7000,7009;StartTime=$began} -ErrorAction SilentlyContinue |
        Select-Object TimeCreated,Id,Message | ConvertTo-Json | Set-Content "$artifacts\scm-events.json"
    if(Get-Service SSC.Agent -ErrorAction SilentlyContinue){Stop-Service SSC.Agent -ErrorAction SilentlyContinue;& sc.exe delete SSC.Agent | Out-Null}
    foreach($cert in @($agentCert,$clientCert)){if($cert -and (Test-Path "Cert:\LocalMachine\My\$($cert.Thumbprint)")){Remove-Item "Cert:\LocalMachine\My\$($cert.Thumbprint)" -DeleteKey}}
}
