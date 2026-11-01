using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using SSC.Infrastructure;
using Xunit;
namespace SSC.IntegrationTests;
public sealed class TlsCertificateLoaderTests
{
    internal sealed class Secret(string value) : ISecretStore
    {
        public List<string> Names { get; } = [];
        public string Read(string name) { Names.Add(name);return value; }
    }
    internal sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState,Exception?,string> formatter) => Messages.Add(formatter(state,exception));
    }
    internal static X509Certificate2 Create(string name, string eku, bool san = false, DateTimeOffset? until = null)
    {
        using var rsa=RSA.Create(2048);
        var request=new CertificateRequest("CN="+name,rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(eku) },true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
        if(san) { var builder=new SubjectAlternativeNameBuilder();builder.AddIpAddress(System.Net.IPAddress.Loopback);request.CertificateExtensions.Add(builder.Build()); }
        // Test-only Exportable supports writing fixture PFXs; production imports are never exportable.
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2),until??DateTimeOffset.UtcNow.AddHours(1));
        return new(cert.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }
    [Fact] public void WindowsUsesMachineKeysAndOtherPlatformsKeepEphemeralKeys()
    {
        Assert.Equal(OperatingSystem.IsWindows()?X509KeyStorageFlags.MachineKeySet:X509KeyStorageFlags.EphemeralKeySet,TlsCertificateLoader.StorageFlags);
        Assert.Equal((X509KeyStorageFlags)0,TlsCertificateLoader.StorageFlags & (X509KeyStorageFlags.PersistKeySet|X509KeyStorageFlags.Exportable));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void LoadPreservesIdentityAndKeyRemainsUsable(bool san)
    {
        using var original=Create("existing-agent","1.3.6.1.5.5.7.3.1",san);
        var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".pfx");
        File.WriteAllBytes(file,original.Export(X509ContentType.Pfx,"fixture-password"));
        try
        {
            var secret=new Secret("fixture-password");
            using var loaded=TlsCertificateLoader.Load(file,secret,"agent-pfx",TlsCertificatePurpose.Server);
            Assert.Equal(original.GetCertHashString(HashAlgorithmName.SHA256),loaded.GetCertHashString(HashAlgorithmName.SHA256));
            Assert.Equal("agent-pfx",Assert.Single(secret.Names));
            using var rsa=loaded.GetRSAPrivateKey();Assert.NotNull(rsa);
            using var publicKey=original.GetRSAPublicKey();var message=RandomNumberGenerator.GetBytes(32);
            Assert.True(publicKey!.VerifyData(message,rsa.SignData(message,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1));
            if(OperatingSystem.IsWindows() && rsa is RSACng cng) Assert.False(cng.Key.IsEphemeral);
        }
        finally { File.Delete(file); }
    }
    [Fact] public void WrongPasswordFailsAndLogsOnlySafeDiagnostics()
    {
        using var original=Create("agent","1.3.6.1.5.5.7.3.1");
        var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".pfx");File.WriteAllBytes(file,original.Export(X509ContentType.Pfx,"correct-password"));
        var logger=new RecordingLogger<object>();
        try
        {
            Assert.Throws<CryptographicException>(()=>TlsCertificateLoader.Load(file,new Secret("SECRET-MUST-NOT-APPEAR"),"agent-pfx",TlsCertificatePurpose.Server,logger));
            Assert.Contains(logger.Messages,m=>m.Contains("stage=PFX import"));Assert.DoesNotContain(logger.Messages,m=>m.Contains("SECRET-MUST-NOT-APPEAR")||m.Contains(file));
            TlsCertificateLoader.LogFailure(logger,"test","handshake",new HttpRequestException("https://discord.com/api/webhooks/SECRET",new InvalidOperationException("PASSWORD")));
            Assert.DoesNotContain(logger.Messages,m=>m.Contains("SECRET")||m.Contains("PASSWORD"));
        }
        finally { File.Delete(file); }
    }
    [Fact] public void PublicOnlyAndWrongPurposeCertificatesAreRejected()
    {
        using var server=Create("agent","1.3.6.1.5.5.7.3.1");
        var file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".pfx");
        try
        {
            File.WriteAllBytes(file,server.Export(X509ContentType.Cert));
            Assert.Throws<CryptographicException>(()=>TlsCertificateLoader.Load(file,new Secret(""),"agent-pfx",TlsCertificatePurpose.Server));
            File.WriteAllBytes(file,server.Export(X509ContentType.Pfx,"pass"));
            Assert.Throws<CryptographicException>(()=>TlsCertificateLoader.Load(file,new Secret("pass"),"agent-pfx",TlsCertificatePurpose.Client));
        }
        finally { File.Delete(file); }
    }
    [Fact] public void PeerValidationRejectsExpiredAndWrongEkuWithoutTrustBypass()
    {
        using var expired=Create("expired","1.3.6.1.5.5.7.3.1",until:DateTimeOffset.UtcNow.AddDays(-1));
        Assert.False(CertificatePins.ValidatePeer(expired,expired.GetCertHashString(HashAlgorithmName.SHA256),TlsCertificatePurpose.Server));
        using var client=Create("client","1.3.6.1.5.5.7.3.2");
        Assert.False(CertificatePins.ValidatePeer(client,client.GetCertHashString(HashAlgorithmName.SHA256),TlsCertificatePurpose.Server));
        Assert.False(CertificatePins.ValidatePeer(client,new('0',64),TlsCertificatePurpose.Client));
        Assert.False(CertificatePins.ValidatePeer(null,new('0',64),TlsCertificatePurpose.Client));
    }
}
