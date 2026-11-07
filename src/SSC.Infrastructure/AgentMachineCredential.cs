using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
namespace SSC.Infrastructure;

public sealed record AgentCredentialReference(string CertificateSha256,string SourcePfxSha256,string SourcePfxPath);
public sealed record AgentCredentialPreparation(string CertificateSha256,string Provider,bool StoreEntryCreated,string ReferencePath);

// Administrator creates the persistent key; the service only reads a pinned existing key.
// Existing service.json, PFX and DPAPI files are never changed by this component.
public static class AgentMachineCredential
{
    public static string ReferencePath => Path.Combine(ServicePaths.Root,"Credentials","Agent","identity.json");
    public static X509Certificate2 Load(HeartbeatOptions options,ILogger? logger,Action<string> checkpoint)
    {
        if(!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Provisioned Agent credentials require Windows.");
        checkpoint("CredentialReferenceReadBegin");
        var reference=JsonSerializer.Deserialize<AgentCredentialReference>(File.ReadAllText(ReferencePath)) ?? throw new InvalidDataException("Invalid Agent credential reference.");
        if(!CertificatePins.Valid(reference.CertificateSha256) ||
            !string.Equals(Path.GetFullPath(options.CertificatePath),reference.SourcePfxPath,StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.CertificatePath))),reference.SourcePfxSha256,StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Configured PFX differs from the administrator-prepared credential. Prepare it again before deployment.");
        checkpoint("CredentialReferenceReadComplete");
        var certificate=Find(reference.CertificateSha256) ?? throw new CryptographicException("Prepared machine certificate is missing; run Prepare-SscAgentTls.ps1.");
        try
        {
            checkpoint("StoreKeyOpenBegin");
            TlsCertificateLoader.ValidateSigningCertificate(certificate,TlsCertificatePurpose.Server,logger,checkpoint,"ProvisionedLocalMachineStore");
            checkpoint("StoreKeyOpenComplete");return certificate;
        }
        catch { certificate.Dispose();throw; }
    }
    [SupportedOSPlatform("windows")]
    private static X509Certificate2? Find(string sha256)
    {
        using var store=new X509Store(StoreName.My,StoreLocation.LocalMachine);store.Open(OpenFlags.ReadOnly|OpenFlags.OpenExistingOnly);
        X509Certificate2? selected=null;
        foreach(var candidate in store.Certificates)
        {
            if(selected is null && CertificatePins.Matches(candidate,sha256) && candidate.HasPrivateKey)selected=candidate;
            else candidate.Dispose();
        }
        return selected;
    }
    public static AgentCredentialPreparation Prepare(HeartbeatOptions options,ISecretStore secrets,string expectedServerSha256)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException();
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("Administrator preparation required.");
        var serviceSid=(SecurityIdentifier)new NTAccount("NT SERVICE\\SSC.Agent").Translate(typeof(SecurityIdentifier));
        var password=secrets.Read(options.CertificatePasswordSecret);
        using var expected=new X509Certificate2(options.CertificatePath,password,X509KeyStorageFlags.EphemeralKeySet);
        if(TlsCertificateLoader.PeerRejection(expected,TlsCertificatePurpose.Server) is { } rejection)throw new CryptographicException(rejection);
        var pin=expected.GetCertHashString(HashAlgorithmName.SHA256);
        if(!CertificatePins.Matches(expected,expectedServerSha256))throw new CryptographicException("Existing PFX does not match the expected public server pin. No credential is prepared.");
        var directory=PrepareReferenceDirectory(serviceSid);
        RejectReparsePoint(ReferencePath);
        if(File.Exists(ReferencePath))
        {
            var previous=JsonSerializer.Deserialize<AgentCredentialReference>(File.ReadAllText(ReferencePath));
            if(previous?.CertificateSha256!=pin)throw new CryptographicException("Preparation refuses an identity change; coordinate any separate rotation explicitly.");
        }
        var certificate=Find(pin);var created=certificate is null;
        certificate ??= new X509Certificate2(options.CertificatePath,password,X509KeyStorageFlags.MachineKeySet|X509KeyStorageFlags.PersistKeySet);
        using(certificate)
        {
            if(!CertificatePins.Matches(certificate,pin))throw new CryptographicException("Configured PFX identity changed during preparation.");
            var (provider,path)=KeyFile(certificate);
            var keyInfo=new FileInfo(path);var acl=keyInfo.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(serviceSid,FileSystemRights.Read,AccessControlType.Allow));keyInfo.SetAccessControl(acl);
            TlsCertificateLoader.ValidateSigningCertificate(certificate,TlsCertificatePurpose.Server,null,_=>{});
            if(created){using var store=new X509Store(StoreName.My,StoreLocation.LocalMachine);store.Open(OpenFlags.ReadWrite);store.Add(certificate);}
            var reference=new AgentCredentialReference(pin,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.CertificatePath))),Path.GetFullPath(options.CertificatePath));
            // Use a new file inside the protected directory: never follow or truncate a
            // pre-existing .tmp file, and protect the new reference before publishing it.
            var temp=Path.Combine(directory,".identity-"+Guid.NewGuid().ToString("N")+".tmp");
            var fileAcl=new FileSecurity();fileAcl.SetAccessRuleProtection(true,false);
            fileAcl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
            foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
                fileAcl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,AccessControlType.Allow));
            fileAcl.AddAccessRule(new FileSystemAccessRule(serviceSid,FileSystemRights.Read,AccessControlType.Allow));
            try
            {
                using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    JsonSerializer.Serialize(stream,reference);stream.Flush(true);
                }
                new FileInfo(temp).SetAccessControl(fileAcl);
                RejectReparsePoint(ReferencePath);
                File.Move(temp,ReferencePath,true);
            }
            finally { if(File.Exists(temp))File.Delete(temp); }
            return new(pin,provider,created,ReferencePath);
        }
    }
    [SupportedOSPlatform("windows")]
    private static string PrepareReferenceDirectory(SecurityIdentifier serviceSid)
    {
        // Keep existing ProgramData/config/secret ACLs untouched. Only the new
        // credential subtree is protected; reject redirection before changing it.
        RejectReparsePoint(ServicePaths.Root);
        var credentials=Path.Combine(ServicePaths.Root,"Credentials");
        var directory=Path.GetDirectoryName(ReferencePath)!;
        foreach(var path in new[]{credentials,directory})
        {
            RejectReparsePoint(path);Directory.CreateDirectory(path);RejectReparsePoint(path);
            var permissions=new DirectorySecurity();permissions.SetAccessRuleProtection(true,false);
            // Replacing a DACL alone leaves a pre-existing owner with WRITE_DAC.
            permissions.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null));
            foreach(var sid in new[]{new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
                permissions.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            permissions.AddAccessRule(new FileSystemAccessRule(serviceSid,FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(permissions);
        }
        return directory;
    }
    private static void RejectReparsePoint(string path)
    {
        try
        {
            if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("Credential preparation refuses reparse-point paths.");
        }
        catch(FileNotFoundException) { }
        catch(DirectoryNotFoundException) { }
    }
    [SupportedOSPlatform("windows")]
    public static (string Provider,string Path) KeyFile(X509Certificate2 certificate)
    {
        using var rsa=certificate.GetRSAPrivateKey();
        Console.WriteLine(JsonSerializer.Serialize(new { Diagnostic="CredentialProviderInspection", RsaType=rsa?.GetType().Name,
            CngProvider=(rsa as RSACng)?.Key.Provider?.Provider, CngMachine=(rsa as RSACng)?.Key.IsMachineKey,
            CspProvider=(rsa as RSACryptoServiceProvider)?.CspKeyContainerInfo.ProviderName,
            CspMachine=(rsa as RSACryptoServiceProvider)?.CspKeyContainerInfo.MachineKeyStore }));
        if(rsa is RSACng cng && cng.Key.IsMachineKey)
        {
            var unique=cng.Key.UniqueName ?? throw new CryptographicException("Machine key has no unique name.");
            if(Path.GetFileName(unique)!=unique)throw new CryptographicException("Unexpected private-key file name.");
            var provider=cng.Key.Provider?.Provider;
            if(provider==CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider)
                return("CNG",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Microsoft","Crypto","Keys",unique));
            // NCrypt can wrap a legacy Microsoft CSP: RSACng is not proof of CNG file storage.
            if(provider is "Microsoft Enhanced RSA and AES Cryptographic Provider" or "Microsoft Enhanced Cryptographic Provider v1.0" or "Microsoft Strong Cryptographic Provider")
                return("CAPI",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Microsoft","Crypto","RSA","MachineKeys",unique));
        }
        if(rsa is RSACryptoServiceProvider csp && csp.CspKeyContainerInfo.MachineKeyStore)
            return("CAPI",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Microsoft","Crypto","RSA","MachineKeys",csp.CspKeyContainerInfo.UniqueKeyContainerName));
        throw new CryptographicException("Unsupported/hardware key provider; configure provider-specific key permissions explicitly.");
    }
}
