using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;
namespace SSC.Agent;

[SupportedOSPlatform("windows")]
public sealed class AgentWindowsServiceLifetime(IHostEnvironment environment,IHostApplicationLifetime applicationLifetime,
    ILoggerFactory loggerFactory,IOptions<HostOptions> hostOptions,IOptions<WindowsServiceLifetimeOptions> serviceOptions,
    AgentStartupJournal journal) : WindowsServiceLifetime(environment,applicationLifetime,loggerFactory,hostOptions,serviceOptions)
{
    protected override void OnStart(string[] args)
    {
        journal.Write("ScmConnected");
        base.OnStart(args);
    }
}
