using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using SSC.Core;
using SSC.Infrastructure;
using Xunit;
namespace SSC.SecurityTests;
public sealed class BoundaryTests
{
    [Theory] [InlineData("stop")] [InlineData("restart")] [InlineData("cmd /c del world")] [InlineData("open security; stop")] [InlineData("powershell.exe")] [InlineData("open ../world")] [InlineData("help && shutdown")]
    public void TerminalRejectsNonAllowlistedCommands(string command) => Assert.Equal(LocalCommandKind.Invalid,LocalCommandRouter.Parse(command).Kind);
    [Theory] [InlineData(ServerAction.Start)] [InlineData(ServerAction.Stop)] [InlineData(ServerAction.Restart)]
    public async Task LiveControlIsDisabledEvenWhenConfirmed(ServerAction action)
    { await Assert.ThrowsAsync<InvalidOperationException>(()=>new DisabledServerController().RequestAsync(new(Guid.NewGuid(),action,true),CancellationToken.None)); }
    [Theory] [InlineData("Agent:Simulation","false")] [InlineData("Agent:RemoteAdministrationEnabled","true")]
    public void LiveConfigurationFailsClosed(string key,string value)
    {
        Assert.Throws<InvalidOperationException>(()=>new HostBuilder().ConfigureAppConfiguration(c=>c.AddInMemoryCollection(new Dictionary<string,string?>{{key,value}})).AddSsc("test").Build());
    }
}
