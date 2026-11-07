#Requires -Version 7.0
#Requires -RunAsAdministrator
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][string]$HarnessDirectory,[ValidateSet('CNG','CAPI')][string]$Provider='CNG')
$ErrorActionPreference='Stop'
. "$PSScriptRoot\..\..\scripts\Ssc-StartupJournal.ps1"
if($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows'){throw 'Disposable Windows GitHub runner only. Never run this fixture on AM4 or AM5.'}
if(Get-Service SSC.Agent -ErrorAction SilentlyContinue){throw 'SSC.Agent already exists; refusing to touch an existing installation.'}
$root=Join-Path $env:ProgramData 'ShadowServerCommand'
$target=Join-Path $env:ProgramFiles 'ShadowServerCommand\Agent'
if((Test-Path $root) -or (Test-Path $target)){throw 'Existing SSC data/binaries detected; fixture refuses to overwrite them.'}
$began=Get-Date
$artifacts=Join-Path $env:GITHUB_WORKSPACE 'TestResults\service-startup'
New-Item $artifacts -ItemType Directory -Force | Out-Null
$agentCert=$null;$clientCert=$null;$protectedOriginal=$null;$secretRestored=$false;$directoryAcls=@{};$keyAclBefore=$null;$keyFile=$null;$storeCertificateRemoved=$false
$creation=if($Provider -eq 'CNG'){@{Provider='Microsoft Software Key Storage Provider'}}else{@{Provider='Microsoft Enhanced RSA and AES Cryptographic Provider';KeySpec='KeyExchange'}}
try {
    New-Item "$root\Agent" -ItemType Directory -Force | Out-Null
    New-Item "$root\Secrets" -ItemType Directory -Force | Out-Null
    Set-Content "$root\fixture-only.marker" 'Disposable runner fixture only'
    New-Item $target -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $PublishDirectory '*') $target -Recurse -Force
    $agentCert=New-SelfSignedCertificate @creation -Subject 'CN=SSC.ServiceFixture' -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy Exportable -KeyUsage DigitalSignature,KeyEncipherment -Type Custom -TextExtension '2.5.29.37={text}1.3.6.1.5.5.7.3.1' -NotAfter (Get-Date).AddDays(1)
    $clientCert=New-SelfSignedCertificate @creation -Subject 'CN=SSC.ClientFixture' -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy Exportable -KeyUsage DigitalSignature,KeyEncipherment -Type Custom -TextExtension '2.5.29.37={text}1.3.6.1.5.5.7.3.2' -NotAfter (Get-Date).AddDays(1)
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
    if(-not [Diagnostics.EventLog]::SourceExists('SSC.Agent')){[Diagnostics.EventLog]::CreateEventSource('SSC.Agent','Application')}
    & icacls.exe $root /grant 'NT SERVICE\SSC.Agent:(RX)' | Out-Null
    & icacls.exe "$root\Agent" /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' 'NT SERVICE\SSC.Agent:(OI)(CI)M' | Out-Null
    & icacls.exe "$root\Secrets" /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' 'NT SERVICE\SSC.Agent:(OI)(CI)RX' | Out-Null
    & icacls.exe $target /grant 'NT SERVICE\SSC.Agent:(OI)(CI)RX' | Out-Null
    & icacls.exe "$root\fixture-only.marker" /grant 'NT SERVICE\SSC.Agent:R' | Out-Null
    $harnessTarget=Join-Path $env:ProgramFiles 'ShadowServerCommand\Harness'
    New-Item $harnessTarget -ItemType Directory -Force | Out-Null
    Copy-Item (Join-Path $HarnessDirectory '*') $harnessTarget -Recurse -Force
    & icacls.exe $harnessTarget /grant 'NT SERVICE\SSC.Agent:(OI)(CI)RX' | Out-Null
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
    # A wrong public pin must fail before any reference/store binding is established.
    & "$target\SSC.Agent.exe" --prepare-tls ("--expected-server-pin="+('0'*64)) | Out-Null
    if($LASTEXITCODE -eq 0 -or (Test-Path "$root\Credentials\Agent\identity.json")){throw 'Preparation accepted the wrong expected identity.'}
    & "$PSScriptRoot\..\..\scripts\Prepare-SscAgentTls.ps1" -AgentPublishDirectory $PublishDirectory -ExpectedServerSha256 $serverPin
    if($LASTEXITCODE -ne 0){throw 'Administrator credential preparation failed.'}
    $storeCert=Get-ChildItem Cert:\LocalMachine\My | Where-Object {$_.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256) -eq $serverPin} | Select-Object -First 1
    $rsa=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($storeCert)
    if($rsa -is [Security.Cryptography.RSACng]){
        $folder=if($rsa.Key.Provider.Provider -eq 'Microsoft Software Key Storage Provider'){'Microsoft\Crypto\Keys\'}else{'Microsoft\Crypto\RSA\MachineKeys\'}
        $keyFile=Join-Path $env:ProgramData ($folder+$rsa.Key.UniqueName)
    }else{$keyFile=Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\'+$rsa.CspKeyContainerInfo.UniqueKeyContainerName)}
    $rsa.Dispose()
    foreach($protectedFile in @($keyFile,"$root\Credentials","$root\Credentials\Agent","$root\Credentials\Agent\identity.json")){
        $fileAcl=Get-Acl $protectedFile
        $direct=@($fileAcl.Access | Where-Object {$_.IdentityReference.Value -eq 'NT SERVICE\SSC.Agent' -and $_.AccessControlType -eq 'Allow'})
        if(-not $direct.Count -or @($direct | Where-Object {($_.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -ne 0}).Count){throw 'Service received write rights to the prepared private key or credential reference path.'}
        if($protectedFile -ne $keyFile -and $fileAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544'){throw 'Prepared credential path owner is not Administrators.'}
    }
    # Mimic AM4's read-only machine-key directories for this exact test identity.
    # Never replace a global ACL or grant broader rights; restore both in finally.
    foreach($directory in @("$env:ProgramData\Microsoft\Crypto\Keys","$env:ProgramData\Microsoft\Crypto\RSA\MachineKeys")){
        $acl=Get-Acl $directory;$directoryAcls[$directory]=$acl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('NT SERVICE\SSC.Agent',[Security.AccessControl.FileSystemRights]::CreateFiles,[Security.AccessControl.InheritanceFlags]::None,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Deny))
        Set-Acl $directory $acl
    }
    $comparisons=@()
    foreach($mode in @('Machine','Ephemeral')){
        $known=@(Get-SscStartupRecords "$root\Agent\logs" | Where-Object Stage -eq 'ProcessEntry' | ForEach-Object ProcessKey)
        $harness=Join-Path $harnessTarget 'SSC.TlsServiceHarness.exe'
        & sc.exe config SSC.Agent binPath= "`"$harness`" --DiagnosticMode=$mode" | Out-Null
        & sc.exe start SSC.Agent | Out-Null
        Start-Sleep -Seconds 2
        $healthy=$false
        for($attempt=0;$attempt -lt 3;$attempt++){
            try{$response=[SscFixtureHttp]::Get($clientCert,$serverPin).GetAwaiter().GetResult() | ConvertFrom-Json;if($response.agentHealthy){$healthy=$true;break}}catch{}
            if((Get-Service SSC.Agent).Status -eq 'Stopped'){break}
        }
        $records=@(Get-SscStartupRecords "$root\Agent\logs")
        $failures=@(Find-SscFreshStartupFailure -Records $records -KnownProcessKeys $known)
        $freshStarts=@($records|Where-Object {$_.Stage -eq 'ProcessEntry' -and $known -notcontains $_.ProcessKey})
        $modeRecords=@($records|Where-Object {$freshStarts.ProcessId -contains $_.ProcessId})
        $comparison=[pscustomobject]@{Provider=$Provider;Mode=$mode;AuthenticatedHttps=$healthy;ImportSucceeded=[bool]@($modeRecords|Where-Object {$_.Stage -in @('PfxImportComplete','EphemeralImportComplete')}).Count;SigningSucceeded=[bool]@($modeRecords|Where-Object Stage -eq 'SigningProbeComplete').Count;FailureHResult=if($failures.Count){$failures[0].HResult}else{$null}}
        $comparisons+=$comparison;$comparison|ConvertTo-Json -Compress|Write-Host
        if($mode -eq 'Machine' -and ($healthy -or $failures.Count -ne 1 -or $comparison.FailureHResult -ne '0x80090010' -or $comparison.ImportSucceeded -or $comparison.SigningSucceeded)){throw 'Restricted machine-key import did not reproduce NTE_PERM 0x80090010 before import/signing completed.'}
        Stop-Service SSC.Agent -ErrorAction SilentlyContinue
        (Get-Service SSC.Agent).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
    }
    $comparisons | ConvertTo-Json | Set-Content "$artifacts\provider-comparison.json"
    & sc.exe config SSC.Agent binPath= "`"$target\SSC.Agent.exe`"" | Out-Null
    $timer=[Diagnostics.Stopwatch]::StartNew()
    & sc.exe start SSC.Agent | Out-File "$artifacts\sc-start.txt"
    if($LASTEXITCODE -ne 0){throw "SCM rejected startup after $($timer.Elapsed.TotalSeconds) seconds."}
    (Get-Service SSC.Agent).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
    $scmSeconds=$timer.Elapsed.TotalSeconds
    if($scmSeconds -ge 45){throw 'SCM handshake exceeded the 45-second cold-process regression budget.'}

    $deadline=(Get-Date).AddSeconds(45);$heartbeat=$null
    while((Get-Date) -lt $deadline){
        try {$heartbeat=[SscFixtureHttp]::Get($clientCert,$serverPin).GetAwaiter().GetResult() | ConvertFrom-Json;break}catch{Start-Sleep -Seconds 1}
    }
    if(-not $heartbeat -or -not $heartbeat.agentHealthy){throw 'Service Running but authenticated heartbeat did not become healthy.'}
    $beforeIdempotent=(Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'").ProcessId
    & powershell.exe -NoProfile -NonInteractive -File "$PSScriptRoot\..\..\scripts\Prepare-SscAgentTls.ps1" -AgentPublishDirectory $PublishDirectory -ExpectedServerSha256 $serverPin
    if($LASTEXITCODE -ne 0 -or (Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'").ProcessId -ne $beforeIdempotent){throw 'Windows PowerShell 5.1 idempotent preparation failed or disturbed the running service.'}
    $journalDirectory=Join-Path $root 'Agent\logs'
    $entries=@(Get-SscStartupRecords -Directory $journalDirectory)
    # Earlier provider-harness processes also record SCM stages. Compare elapsed
    # times only inside the active production Agent process, never across PIDs.
    $activeStart=@($entries | Where-Object {$_.Stage -eq 'ProcessEntry' -and $_.ProcessId -eq $beforeIdempotent -and $_.Account -eq 'NT SERVICE\SSC.Agent'} | Sort-Object UtcTimestamp -Descending)[0]
    $activeEntries=@($entries | Where-Object {$_.ProcessId -eq $beforeIdempotent -and $_.Build -eq $activeStart.Build -and $_.UtcTimestamp -ge $activeStart.UtcTimestamp})
    $scm=@($activeEntries | Where-Object Stage -eq 'ScmConnected')
    $import=@($activeEntries | Where-Object Stage -eq 'StoreKeyOpenBegin')
    Write-Host ("SCM ordering predicate: activePid={0}; scmRecords={1}; keyRecords={2}; scmElapsed={3}; keyElapsed={4}" -f $beforeIdempotent,$scm.Count,$import.Count,$scm.ElapsedMs,$import.ElapsedMs)
    if(-not $activeStart -or $scm.Count -ne 1 -or $import.Count -ne 1 -or $scm[0].Account -ne 'NT SERVICE\SSC.Agent' -or $scm[0].ElapsedMs -ge 5000 -or $scm[0].ElapsedMs -gt $import[0].ElapsedMs){throw 'Private key work happened before SCM connected for the active Agent process.'}
    $before=Get-FileHash "$root\Agent\service.json","$root\Agent\identity.pfx","$root\Secrets\agent-pfx.bin","$root\Credentials\Agent\identity.json"
    & powershell.exe -NoProfile -NonInteractive -File "$PSScriptRoot\..\..\scripts\Update-SscService.ps1" -Role Agent -PublishDirectory $PublishDirectory -BackupDirectory "$artifacts\update-backup"
    if($LASTEXITCODE -ne 0){throw 'Windows PowerShell 5.1 updater failed.'}
    $after=Get-FileHash "$root\Agent\service.json","$root\Agent\identity.pfx","$root\Secrets\agent-pfx.bin","$root\Credentials\Agent\identity.json"
    if(Compare-Object $before.Hash $after.Hash){throw 'Updater changed production configuration/certificate/secret bytes.'}
    $heartbeat=$null;$deadline=(Get-Date).AddSeconds(35)
    do{try{$heartbeat=[SscFixtureHttp]::Get($clientCert,$serverPin).GetAwaiter().GetResult() | ConvertFrom-Json;break}catch{Start-Sleep -Milliseconds 500}}while((Get-Date) -lt $deadline)
    if(-not $heartbeat.agentHealthy){throw 'Updated service lost authenticated heartbeat.'}
    # Wrong DPAPI password must fail administrator preparation while the existing service stays running.
    # Restore the encrypted bytes afterward; this fixture never uses production secrets.
    $protectedOriginal=[IO.File]::ReadAllBytes("$root\Secrets\agent-pfx.bin")
    $beforePreflightPid=(Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'").ProcessId
    $bad=[Text.Encoding]::UTF8.GetBytes('intentionally-wrong-fixture-password')
    [IO.File]::WriteAllBytes("$root\Secrets\agent-pfx.bin",[Security.Cryptography.ProtectedData]::Protect($bad,$null,[Security.Cryptography.DataProtectionScope]::LocalMachine))
    [Array]::Clear($bad,0,$bad.Length)
    try {
        & "$target\SSC.Agent.exe" --prepare-tls "--expected-server-pin=$serverPin" | Out-Null
        if($LASTEXITCODE -eq 0){throw 'Wrong password incorrectly accepted during preparation.'}
        if((Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'").ProcessId -ne $beforePreflightPid){throw 'Preparation disturbed the running Agent.'}
    } finally {[IO.File]::WriteAllBytes("$root\Secrets\agent-pfx.bin",$protectedOriginal);$secretRestored=$true}
    Stop-Service SSC.Agent
    $keyAclBefore=(Get-Acl $keyFile).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
    $denied=Get-Acl $keyFile
    $denied.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('NT SERVICE\SSC.Agent',[Security.AccessControl.FileSystemRights]::Read,[Security.AccessControl.AccessControlType]::Deny))
    Set-Acl $keyFile $denied
    $knownProcessKeys=@(Get-SscStartupRecords -Directory $journalDirectory | Where-Object Stage -eq 'ProcessEntry' | ForEach-Object ProcessKey)
    $failureStart=[DateTimeOffset]::UtcNow
    Write-Host ("Failure test begins: utc={0}; knownProcesses={1}" -f $failureStart.ToString('O'),$knownProcessKeys.Count)
    & sc.exe start SSC.Agent | Out-Null
    $deadline=(Get-Date).AddSeconds(30);$failure=$null
    while((Get-Date) -lt $deadline){
        $records=@(Get-SscStartupRecords -Directory $journalDirectory)
        $failure=@(Find-SscFreshStartupFailure -Records $records -KnownProcessKeys $knownProcessKeys)
        if($failure.Count){break};Start-Sleep -Milliseconds 500
    }
    foreach($candidate in @($records | Where-Object Stage -eq 'StartupFailed')){
        Write-Host ("Failure predicate: pid={0}; stage={1}; rawTimestamp={2}; inputType={3}; utc={4}; threshold={5}; afterThreshold={6}; file={7}; selected={8}" -f
            $candidate.ProcessId,$candidate.Stage,$candidate.TimestampUtc,$candidate.TimestampUtc.GetType().FullName,
            $candidate.UtcTimestamp.ToString('O'),$failureStart.ToString('O'),($candidate.UtcTimestamp -ge $failureStart),$candidate.SourceFile,
            ($failure.ProcessKey -contains $candidate.ProcessKey))
    }
    if($failure.Count -ne 1){throw 'Private-key read denial must emit exactly one StartupFailed for the fresh process; see predicate diagnostics.'}
    if($failure[0].ErrorType -ne 'CryptographicException' -or @('0x80090010','0x80070005','0x80090016') -notcontains $failure[0].HResult){throw 'Key-read denial failed for an unexpected reason.'}
    (Get-Service SSC.Agent).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
    $restored=Get-Acl $keyFile;$restored.SetSecurityDescriptorSddlForm($keyAclBefore);Set-Acl $keyFile $restored;$keyAclBefore=$null
    Start-Service SSC.Agent
    $heartbeat=$null;$deadline=(Get-Date).AddSeconds(35)
    do {try{$heartbeat=[SscFixtureHttp]::Get($clientCert,$serverPin).GetAwaiter().GetResult() | ConvertFrom-Json;break}catch{Start-Sleep -Milliseconds 500}}while((Get-Date) -lt $deadline)
    if(-not $heartbeat.agentHealthy){throw 'Service did not recover authenticated heartbeat after private-key access restoration.'}
    # A missing store certificate must fail closed without reimporting the PFX or creating keys.
    Stop-Service SSC.Agent
    (Get-Service SSC.Agent).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
    $store=[Security.Cryptography.X509Certificates.X509Store]::new('My','LocalMachine')
    try{$store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite);$store.Remove($storeCert);$storeCertificateRemoved=$true}finally{$store.Dispose()}
    try {
        $knownProcessKeys=@(Get-SscStartupRecords -Directory $journalDirectory | Where-Object Stage -eq 'ProcessEntry' | ForEach-Object ProcessKey)
        & sc.exe start SSC.Agent | Out-Null
        $deadline=(Get-Date).AddSeconds(30);$failure=@()
        while((Get-Date) -lt $deadline){
            $failure=@(Find-SscFreshStartupFailure -Records @(Get-SscStartupRecords -Directory $journalDirectory) -KnownProcessKeys $knownProcessKeys)
            if($failure.Count){break};Start-Sleep -Milliseconds 500
        }
        if($failure.Count -ne 1 -or $failure[0].ErrorType -ne 'CryptographicException'){throw 'Missing store certificate did not emit one cryptographic startup failure.'}
        (Get-Service SSC.Agent).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
    } finally {
        $store=[Security.Cryptography.X509Certificates.X509Store]::new('My','LocalMachine')
        try{$store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite);$store.Add($storeCert);$storeCertificateRemoved=$false}finally{$store.Dispose()}
    }
    Start-Service SSC.Agent
    $heartbeat=$null;$deadline=(Get-Date).AddSeconds(35)
    do{try{$heartbeat=[SscFixtureHttp]::Get($clientCert,$serverPin).GetAwaiter().GetResult()|ConvertFrom-Json;break}catch{Start-Sleep -Milliseconds 500}}while((Get-Date) -lt $deadline)
    if(-not $heartbeat.agentHealthy){throw 'Restoring the same store certificate did not recover authenticated heartbeat.'}
    # A tampered package must be rejected before stopping the healthy service.
    $corrupt=Join-Path $artifacts 'corrupt-package';New-Item $corrupt -ItemType Directory | Out-Null
    Copy-Item (Join-Path $PublishDirectory '*') $corrupt -Recurse -Force
    [IO.File]::AppendAllText((Join-Path $corrupt 'SSC.Infrastructure.dll'),'fixture corruption')
    $healthyPid=(Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'").ProcessId
    $rejected=$false
    try { & "$PSScriptRoot\..\..\scripts\Update-SscService.ps1" -Role Agent -PublishDirectory $corrupt -BackupDirectory "$artifacts\must-not-exist" }catch{$rejected=$true}
    if(-not $rejected -or (Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'").ProcessId -ne $healthyPid){throw 'Corrupt package was not rejected before stopping the service.'}
    # A hash-valid but nonlaunchable package must restore the previous service automatically.
    $broken=Join-Path $artifacts 'nonlaunchable-package';New-Item $broken -ItemType Directory | Out-Null
    Copy-Item (Join-Path $PublishDirectory '*') $broken -Recurse -Force
    $runtime=Join-Path $broken 'SSC.Agent.runtimeconfig.json'
    (Get-Content $runtime -Raw).Replace('8.0.0','99.0.0') | Set-Content $runtime -Encoding utf8
    Set-Content (Join-Path $broken 'introduced-by-failed-update.txt') 'Fixture-only file that rollback must remove'
    & "$PSScriptRoot\..\..\scripts\Write-SscDeploymentManifest.ps1" -Role Agent -Directory $broken
    $rolledBack=$false
    try{ & "$PSScriptRoot\..\..\scripts\Update-SscService.ps1" -Role Agent -PublishDirectory $broken -BackupDirectory "$artifacts\expected-rollback" }catch{$rolledBack=$true}
    $rollback=Get-Content "$artifacts\expected-rollback\rollback-result.json" -Raw | ConvertFrom-Json
    if(-not $rollback.OperationalVerified -or -not $rollback.HttpsJournalReady){throw 'Updater did not verify previous Agent operational recovery.'}
    if(-not $rolledBack -or (Get-Service SSC.Agent).Status -ne 'Running'){throw 'Failed update did not restore the previous running service.'}
    if(Test-Path (Join-Path $target 'introduced-by-failed-update.txt')){throw 'Rollback left a file introduced by the failed update.'}
    $heartbeat=$null;$deadline=(Get-Date).AddSeconds(35)
    do{try{$heartbeat=[SscFixtureHttp]::Get($clientCert,$serverPin).GetAwaiter().GetResult() | ConvertFrom-Json;break}catch{Start-Sleep -Milliseconds 500}}while((Get-Date) -lt $deadline)
    if(-not $heartbeat.agentHealthy){throw 'Automatic rollback lost authenticated heartbeat.'}
    # Rollback must report failure honestly when the restored key is still inaccessible.
    $keyAclBefore=(Get-Acl $keyFile).GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access)
    try {
        $acl=Get-Acl $keyFile;$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new('NT SERVICE\SSC.Agent',[Security.AccessControl.FileSystemRights]::Read,[Security.AccessControl.AccessControlType]::Deny));Set-Acl $keyFile $acl
        $failedAsExpected=$false
        try{ & "$PSScriptRoot\..\..\scripts\Update-SscService.ps1" -Role Agent -PublishDirectory $broken -BackupDirectory "$artifacts\rollback-operation-failure" }catch{$failedAsExpected=$true}
        $state=Get-Content "$artifacts\rollback-operation-failure\rollback-result.json" -Raw | ConvertFrom-Json
        if(-not $failedAsExpected -or -not $state.BinariesVerified -or $state.OperationalVerified){throw 'Unhealthy rollback was falsely reported as operational.'}
    } finally {$acl=Get-Acl $keyFile;$acl.SetSecurityDescriptorSddlForm($keyAclBefore);Set-Acl $keyFile $acl;$keyAclBefore=$null}
    Start-Service SSC.Agent
    $heartbeat=$null;$deadline=(Get-Date).AddSeconds(35)
    do{try{$heartbeat=[SscFixtureHttp]::Get($clientCert,$serverPin).GetAwaiter().GetResult()|ConvertFrom-Json;break}catch{Start-Sleep -Milliseconds 500}}while((Get-Date) -lt $deadline)
    if(-not $heartbeat.agentHealthy){throw 'Recovery after failed operational rollback did not restore mTLS.'}
    # Do not publish intentionally corrupted binary fixtures as downloadable test artifacts.
    Remove-Item $corrupt,$broken -Recurse -Force
    $consecutive=0
    for($check=0;$check -lt 3;$check++){$reply=[SscFixtureHttp]::Get($clientCert,$serverPin).GetAwaiter().GetResult()|ConvertFrom-Json;if($reply.agentHealthy){$consecutive++}else{$consecutive=0}}
    if($consecutive -ne 3){throw 'Three authenticated heartbeat checks failed.'}
    [pscustomobject]@{MissingCertificateRejected=$true;CertificateRestorationRecovery=$true;WindowsPowerShell51Updater=$true;IntroducedFileRemovedOnRollback=$true;RollbackFailureReported=$true;ThreeAuthenticatedHeartbeats=$consecutive;Provider=$Provider;RestrictedKeyDirectories=$true;FailureDiagnostics=$true;SecretRestorationRecovery=$true;TamperRejectedBeforeStop=$true;AutomaticRollback=$true;ScmSeconds=$scmSeconds;ScmBeforePrivateKeyImport=$true;UpdatePreservedSecrets=$true;Service='SSC.Agent';Identity=(Get-CimInstance Win32_Service -Filter "Name='SSC.Agent'").StartName;StartupSeconds=$timer.Elapsed.TotalSeconds;AuthenticatedHeartbeat=$true;ObservedAt=$heartbeat.observedAt} |
        ConvertTo-Json | Set-Content "$artifacts\result.json"
    Get-Content "$artifacts\result.json"
} finally {
    Stop-Service SSC.Agent -ErrorAction SilentlyContinue
    if($storeCertificateRemoved){
        $store=[Security.Cryptography.X509Certificates.X509Store]::new('My','LocalMachine')
        try{$store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite);$store.Add($storeCert)}finally{$store.Dispose()}
    }
    if($keyFile -and $keyAclBefore){$acl=Get-Acl $keyFile;$acl.SetSecurityDescriptorSddlForm($keyAclBefore);Set-Acl $keyFile $acl}
    foreach($directory in $directoryAcls.Keys){$acl=Get-Acl $directory;$acl.SetSecurityDescriptorSddlForm($directoryAcls[$directory]);Set-Acl $directory $acl}
    # Restore fixture credentials even when the assertion/polling fails. Never leave a bad
    # secret behind for cleanup, retries or later phases of the disposable runner.
    if($null -ne $protectedOriginal -and -not $secretRestored){
        Stop-Service SSC.Agent -ErrorAction SilentlyContinue
        [IO.File]::WriteAllBytes("$root\Secrets\agent-pfx.bin",$protectedOriginal)
        Write-Host 'Original encrypted fixture secret restored in finally.'
    }
    if(Test-Path "$root\Agent\logs"){Get-ChildItem "$root\Agent\logs\startup-*.jsonl" | ForEach-Object {Get-Content $_.FullName};Copy-Item "$root\Agent\logs" "$artifacts\logs" -Recurse -Force}
    Get-WinEvent -FilterHashtable @{LogName='System';Id=7000,7009;StartTime=$began} -ErrorAction SilentlyContinue |
        Select-Object TimeCreated,Id,Message | ConvertTo-Json | Set-Content "$artifacts\scm-events.json"
    if(Get-Service SSC.Agent -ErrorAction SilentlyContinue){Stop-Service SSC.Agent -ErrorAction SilentlyContinue;& sc.exe delete SSC.Agent | Out-Null}
    foreach($cert in @($agentCert,$clientCert)){if($cert -and (Test-Path "Cert:\LocalMachine\My\$($cert.Thumbprint)")){Remove-Item "Cert:\LocalMachine\My\$($cert.Thumbprint)" -DeleteKey}}
}
