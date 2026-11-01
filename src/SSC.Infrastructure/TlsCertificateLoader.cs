using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
namespace SSC.Infrastructure;

public enum TlsCertificatePurpose { Server, Client }

public static class TlsCertificateLoader
{
    // Schannel needs a key container it can acquire, not a PKCS12_NO_PERSIST_KEY import.
    // No PersistKeySet: the certificate owns the temporary machine key until Dispose.
    // No Exportable: production services never need to export the private key.
    public static X509KeyStorageFlags StorageFlags => OperatingSystem.IsWindows()
        ? X509KeyStorageFlags.MachineKeySet : X509KeyStorageFlags.EphemeralKeySet;

    public static X509Certificate2 Load(string path, ISecretStore secrets, string secretName,
        TlsCertificatePurpose purpose, ILogger? logger = null)
    {
        X509Certificate2? certificate = null;
        var stage = "DPAPI secret retrieval";
        try
        {
            var password = secrets.Read(secretName);
            stage = "PFX import";
            certificate = new X509Certificate2(path, password, StorageFlags);
            stage = "certificate/private-key validation";
            if (!certificate.HasPrivateKey) throw new CryptographicException("TLS certificate has no private key.");
            if (PeerRejection(certificate, purpose) is { } reason) throw new CryptographicException(reason);
            var challenge = RandomNumberGenerator.GetBytes(32);
            string algorithm;
            int keySize;
            using (var rsa = certificate.GetRSAPrivateKey())
            {
                if (rsa is not null)
                {
                    using var publicKey = certificate.GetRSAPublicKey();
                    if (publicKey is null || !publicKey.VerifyData(challenge, rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                        throw new CryptographicException("Private/public key mismatch.");
                    algorithm = "RSA"; keySize = rsa.KeySize;
                }
                else
                {
                    using var key = certificate.GetECDsaPrivateKey();
                    using var publicKey = certificate.GetECDsaPublicKey();
                    if (key is null || publicKey is null || !publicKey.VerifyData(challenge, key.SignData(challenge, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256))
                        throw new CryptographicException("Unsupported or inaccessible TLS signing key.");
                    algorithm = "ECDSA"; keySize = key.KeySize;
                }
            }
            logger?.LogInformation("TLS certificate ready: purpose={Purpose}, storage={StorageFlags}, sha256={CertificateSha256}, algorithm={Algorithm}, bits={KeySize}, expiresUtc={ExpiresUtc}, sanPresent={SanPresent}",
                purpose, StorageFlags, certificate.GetCertHashString(HashAlgorithmName.SHA256), algorithm, keySize,
                certificate.NotAfter.ToUniversalTime(), certificate.Extensions.Any(e => e.Oid?.Value == "2.5.29.17"));
            return certificate;
        }
        catch (Exception ex)
        {
            certificate?.Dispose();
            LogFailure(logger, "TLS credential initialization", stage, ex);
            throw;
        }
    }

    public static string? PeerRejection(X509Certificate2 certificate, TlsCertificatePurpose purpose)
    {
        var now = DateTime.UtcNow;
        if (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now) return "CertificateOutsideValidity";
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        var required = purpose == TlsCertificatePurpose.Server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2";
        if (eku is not null && !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == required || o.Value == "2.5.29.37.0")) return "WrongExtendedKeyUsage";
        var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (usage is not null && (usage.KeyUsages & X509KeyUsageFlags.DigitalSignature) == 0) return "DigitalSignatureUsageMissing";
        return null;
    }

    public static void LogFailure(ILogger? logger, string operation, string stage, Exception exception)
    {
        // Deliberately omit Message, ToString, file paths and exception objects: credentials/URLs must never escape.
        for (Exception? current = exception; current is not null; current = current.InnerException)
            logger?.LogError("{Operation} failed: stage={Stage}, errorType={ErrorType}, hresult={HResult}", operation, stage, current.GetType().Name, $"0x{current.HResult:X8}");
    }
}
