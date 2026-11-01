using SSC.Core;
using SSC.Infrastructure;
using Xunit;
namespace SSC.UnitTests;
public sealed class TelemetryTests
{
    [Fact] public async Task SimulationNeverClaimsTickTelemetry()
    {
        var snapshot=await new SimulatedTelemetrySource().ReadAsync(CancellationToken.None);
        Assert.Equal(DataOrigin.Simulated,snapshot.Origin);
        foreach(var name in new[]{"TPS","MSPT"})
        {
            var metric=Assert.Single(snapshot.Metrics,m=>m.Name==name);
            Assert.Equal(MetricState.Unavailable,metric.State); Assert.Null(metric.Value);
            Assert.Contains("UNAVAILABLE",metric.Display);
        }
    }
    [Fact] public void NavigationIncludesWatchdogAndTenExistingModules()
    { Assert.Equal(11,Modules.All.Count); Assert.Equal(11,Modules.All.Select(m=>m.Id).Distinct().Count()); }
    [Theory] [InlineData("open security",LocalCommandKind.Navigate)] [InlineData("help",LocalCommandKind.Help)] [InlineData("crt off",LocalCommandKind.Crt)]
    public void LocalCommandsAreParsed(string input,LocalCommandKind expected) => Assert.Equal(expected,LocalCommandRouter.Parse(input).Kind);
    [Fact] public async Task CancellationStopsSimulationRead()
    { await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new SimulatedTelemetrySource().ReadAsync(new CancellationToken(true))); }
}
