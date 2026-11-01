using System.Net;
using System.Net.Security;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SSC.Core;
namespace SSC.Infrastructure;
public sealed record HeartbeatOptions
{
    public bool Enabled { get; init; }
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 27443;
    public string CertificatePath { get; init; } = "";
    public string CertificatePasswordSecret { get; init; } = "agent-pfx";
    public string TrustedClientSha256 { get; init; } = "";
    public string MinecraftCommandLineContains { get; init; } = "";
    public int? MinecraftProcessId { get; init; }
    public string MinecraftExecutablePath { get; init; } = "";
    public DateTimeOffset? MinecraftProcessStartedAt { get; init; }
    public void Validate()
    {
        if (!PrivateNetwork.IsPrivate(BindAddress) || Port is < 1024 or > 65535 || !CertificatePins.Valid(TrustedClientSha256))
            throw new InvalidOperationException("Agent requires a private IPv4 bind address, unprivileged port and SHA256 client certificate pin.");
        if (MinecraftProcessId.HasValue && (string.IsNullOrWhiteSpace(MinecraftExecutablePath) || MinecraftProcessStartedAt is null))
            throw new InvalidOperationException("Process tracking requires PID, exact executable path and UTC process start time to prevent PID reuse.");
    }
}
public sealed record WatchdogOptions
{
    public bool Enabled { get; init; }
    public string HostAddress { get; init; } = "127.0.0.1";
    public int AgentPort { get; init; } = 27443;
    public int MinecraftPort { get; init; } = 25565;
    public bool MinecraftStatusEnabled { get; init; } = true;
    public int ProbeTimeoutSeconds { get; init; } = 4;
    public string ClientCertificatePath { get; init; } = "";
    public string CertificatePasswordSecret { get; init; } = "watchdog-pfx";
    public string TrustedServerSha256 { get; init; } = "";
    public DetectionOptions Detection { get; init; } = new();
    public void Validate()
    {
        Detection.Validate();
        if (!PrivateNetwork.IsPrivate(HostAddress) || AgentPort is < 1 or > 65535 || MinecraftPort is < 1 or > 65535 || ProbeTimeoutSeconds is < 1 or > 10 || !CertificatePins.Valid(TrustedServerSha256))
            throw new InvalidOperationException("Watchdog requires private IPv4 host address, valid ports/timeouts and SHA256 server certificate pin.");
    }
}
public static class ServicePaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ShadowServerCommand");
    public static string Watchdog => Path.Combine(Root, "Watchdog");
    public static string Dashboard => Path.Combine(Root, "Status", "watchdog.json");
    public static string Secrets => Path.Combine(Root, "Secrets");
}
public static class PrivateNetwork
{
    public static bool IsPrivate(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b=ip.GetAddressBytes(); return b[0]==10 || b[0]==127 || (b[0]==172 && b[1]>=16 && b[1]<=31) || (b[0]==192 && b[1]==168);
    }
}
public static class CertificatePins
{
    public static bool Valid(string pin) => pin.Length==64 && pin.All(Uri.IsHexDigit);
    public static bool Matches(X509Certificate2? certificate, string pin) => certificate is not null && Valid(pin) &&
        certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
        CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(pin));
    public static bool ValidatePeer(X509Certificate2? certificate, string pin, TlsCertificatePurpose purpose,
        ILogger? logger = null, X509Chain? chain = null, SslPolicyErrors errors = SslPolicyErrors.None)
    {
        var reason = certificate is null ? "CertificateMissing" : !Valid(pin) ? "ConfiguredPinInvalid" :
            !Matches(certificate, pin) ? "FingerprintMismatchOrExpired" : TlsCertificateLoader.PeerRejection(certificate, purpose);
        if ((errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) reason = "CertificateMissing";
        if (reason is not null)
            logger?.LogWarning("TLS peer rejected: purpose={Purpose}, reason={Reason}, policyErrors={PolicyErrors}, chainStatus={ChainStatus}",
                purpose, reason, errors, chain is null ? "unavailable" : string.Join(",", chain.ChainStatus.Select(s => s.Status.ToString())));
        else
            logger?.LogDebug("TLS peer accepted by SHA256 pin: purpose={Purpose}, policyErrors={PolicyErrors}", purpose, errors);
        // The existing pinned leaf, validity and EKU are the trust policy. SAN/name and CA-chain
        // errors do not replace that policy; never accept an unpinned peer, even if CA-trusted.
        return reason is null;
    }

}
public interface ISecretStore { string Read(string name); }
public sealed class MachineSecretStore : ISecretStore
{
    public string Read(string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Production secret storage requires Windows DPAPI.");
        if (name.Length==0 || name.Any(c=> !char.IsAsciiLetterOrDigit(c) && c!='-')) throw new ArgumentException("Invalid secret name.");
        var protectedBytes=File.ReadAllBytes(Path.Combine(ServicePaths.Secrets,name+".bin"));
        var bytes=ProtectedData.Unprotect(protectedBytes,null,DataProtectionScope.LocalMachine);
        try { return System.Text.Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
