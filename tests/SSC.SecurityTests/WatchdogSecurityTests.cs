using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SSC.Infrastructure;
using Xunit;
namespace SSC.SecurityTests;
public sealed class WatchdogSecurityTests
{
    [Theory] [InlineData("0.0.0.0")] [InlineData("8.8.8.8")] [InlineData("example.com")] [InlineData("::")]
    public void PublicOrWildcardBindsAreRejected(string address) => Assert.Throws<InvalidOperationException>(()=>new HeartbeatOptions { BindAddress=address,TrustedClientSha256=new('A',64) }.Validate());
    [Theory] [InlineData("http://discord.com/api/webhooks/1/token")] [InlineData("https://evil.example/api/webhooks/1/token")] [InlineData("https://discord.com.evil.example/api/webhooks/1/token")] [InlineData("https://discord.com/api/webhooks/1/token?redirect=evil")]
    public void WebhookMustUseExactHttpsDiscordHost(string url) => Assert.False(DiscordDelivery.IsDiscordWebhook(new(url)));
    [Fact] public void PinsRejectWrongCertificateAndAcceptExactValidIdentity()
    {
        using var rsa=RSA.Create(2048);var request=new CertificateRequest("CN=test",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        using var cert=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        Assert.True(CertificatePins.Matches(cert,cert.GetCertHashString(HashAlgorithmName.SHA256)));
        Assert.False(CertificatePins.Matches(cert,new('0',64)));Assert.False(CertificatePins.Matches(null,new('0',64)));
    }
}
