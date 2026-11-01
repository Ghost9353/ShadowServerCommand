using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using SSC.Infrastructure;
namespace SSC.Agent;
public static class HeartbeatEndpoint
{
    public static void Configure(KestrelServerOptions kestrel,HeartbeatOptions options,X509Certificate2 certificate, ILogger? logger = null)
    {
        options.Validate();
        kestrel.AddServerHeader=false;kestrel.Limits.MaxRequestBodySize=1024;
        kestrel.Listen(IPAddress.Parse(options.BindAddress),options.Port,listen=>listen.UseHttps(https=>
        {
            https.ServerCertificate=certificate;https.SslProtocols=SslProtocols.Tls12 | SslProtocols.Tls13;
            https.ClientCertificateMode=ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation=(client,chain,errors)=>CertificatePins.ValidatePeer(client,options.TrustedClientSha256,TlsCertificatePurpose.Client,logger,chain,errors);
        }));
    }
    public static void Map(WebApplication app,HeartbeatOptions options) => app.MapGet("/heartbeat",(HttpContext context,WindowsHeartbeatCollector collector)=>
    {
        context.Response.Headers.CacheControl="no-store";
        return Results.Json(collector.Read(options));
    });
}
