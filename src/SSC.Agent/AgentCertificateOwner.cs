using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using SSC.Infrastructure;
namespace SSC.Agent;

public sealed class AgentCertificateOwner(HeartbeatOptions options,ISecretStore secrets,AgentStartupJournal journal,
    ILogger<AgentCertificateOwner> logger) : IDisposable
{
    private X509Certificate2? _certificate;
    public X509Certificate2 Certificate => _certificate ?? throw new AuthenticationException("TLS credential is not initialized.");
    public async Task InitializeAsync(CancellationToken ct)
    {
        journal.Write("CertificateInitializationBegin");
        var load=Task.Run(()=>TlsCertificateLoader.Load(options.CertificatePath,secrets,options.CertificatePasswordSecret,
            TlsCertificatePurpose.Server,logger,stage=>journal.Write(stage)),CancellationToken.None);
        try
        {
            _certificate=await load.WaitAsync(TimeSpan.FromSeconds(20),ct);
            journal.Write("CertificateInitializationReady");
        }
        catch(Exception ex)
        {
            journal.Write("CertificateInitializationFailed",ex);
            _=load.ContinueWith(t=> { if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();else _=t.Exception; },TaskScheduler.Default);
            throw;
        }
    }
    public void Dispose() => _certificate?.Dispose();
}

// Generic Host starts hosted services only after WindowsServiceLifetime.WaitForStartAsync.
// Registered before the web-host service, so HTTPS is never declared ready without credentials.
public sealed class AgentCertificateInitializer(AgentCertificateOwner owner) : IHostedService
{
    public Task StartAsync(CancellationToken ct) => owner.InitializeAsync(ct);
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
