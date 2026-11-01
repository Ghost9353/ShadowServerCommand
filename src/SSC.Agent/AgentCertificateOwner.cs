using System.Security.Cryptography.X509Certificates;
using SSC.Infrastructure;
namespace SSC.Agent;

public sealed class AgentCertificateOwner(HeartbeatOptions options,ISecretStore secrets,AgentStartupJournal journal,
    ILogger<AgentCertificateOwner> logger) : IDisposable
{
    private X509Certificate2? _certificate;
    public X509Certificate2 GetCertificate()
    {
        if(_certificate is not null)return _certificate;
        journal.Write("CertificateInitializationBegin");
        var load=Task.Run(()=>TlsCertificateLoader.Load(options.CertificatePath,secrets,options.CertificatePasswordSecret,
            TlsCertificatePurpose.Server,logger,stage=>journal.Write(stage)));
        try
        {
            _certificate=load.WaitAsync(TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
            journal.Write("CertificateInitializationReady");return _certificate;
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
