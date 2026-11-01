using Microsoft.Extensions.Logging.Abstractions;
using SSC.Core;
using SSC.Desktop;
using SSC.Infrastructure;
using Xunit;
namespace SSC.UnitTests;
public sealed class TerminalWorkflowTests
{
    private sealed class TestStore : IMetricStore
    {
        public bool Fail { get; set; }
        public List<TelemetrySnapshot> Snapshots { get; } = [];
        public Task InitializeAsync(CancellationToken ct=default) => Fail ? Task.FromException(new IOException("test disk unavailable")) : Task.CompletedTask;
        public Task AppendAsync(TelemetrySnapshot snapshot,CancellationToken ct=default) { Snapshots.Add(snapshot);return Task.CompletedTask; }
        public Task<IReadOnlyList<TelemetrySnapshot>> ReadRecentAsync(int limit,CancellationToken ct=default) => Task.FromResult<IReadOnlyList<TelemetrySnapshot>>(Snapshots.TakeLast(limit).ToArray());
    }
    private static MainViewModel Create(TestStore store) => new(new SimulatedTelemetrySource(),store,NullLogger<MainViewModel>.Instance);
    [Fact] public async Task TerminalNavigatesEveryModuleAndRejectsServerCommands()
    {
        var store=new TestStore(); var model=Create(store);await model.InitializeAsync();
        foreach(var module in Modules.All)
        {
            model.CommandText="open "+module.Id;await model.ExecuteAsync();
            Assert.Equal(module.Id,model.SelectedModule.Definition.Id);
        }
        model.CommandText="stop";await model.ExecuteAsync();
        Assert.Contains("REJECTED",model.TerminalOutput);Assert.Single(store.Snapshots);
    }
    [Fact] public async Task ArchiveFailureIsVisibleAndRefreshRecovers()
    {
        var store=new TestStore { Fail=true };var model=Create(store);
        await model.InitializeAsync();Assert.Contains("ERROR",model.Status);Assert.Empty(store.Snapshots);
        store.Fail=false;await model.RefreshAsync();
        Assert.Contains("archive ready",model.Status);Assert.Single(store.Snapshots);
    }
    [Fact] public async Task FilterAndOptionalCrtWorkThroughLocalWorkflow()
    {
        var model=Create(new());model.CommandText="open console";await model.ExecuteAsync();
        model.Search="spawn";Assert.Single(model.Rows);
        Assert.False(model.CrtEnabled);
        model.CommandText="crt on";await model.ExecuteAsync();Assert.True(model.CrtEnabled);
        model.CommandText="crt off";await model.ExecuteAsync();Assert.False(model.CrtEnabled);
    }
    private sealed class WatchdogSource : IWatchdogStatusSource
    {
        public Task<WatchdogDashboard?> ReadAsync(CancellationToken ct=default) => Task.FromResult<WatchdogDashboard?>(
            new(DateTimeOffset.UtcNow,"RUNNING",new() { Computer=ComputerCondition.Unreachable,Failures=3 },1,0,null,"QUEUED"));
    }
    [Fact] public async Task WatchdogViewShowsRealSourceAndCriticalCondition()
    {
        var model=new MainViewModel(new SimulatedTelemetrySource(),new TestStore(),NullLogger<MainViewModel>.Instance,new WatchdogSource());
        await model.InitializeAsync();model.CommandText="open watchdog";await model.ExecuteAsync();
        Assert.True(model.IsWatchdog);Assert.False(model.ShowMetrics);Assert.True(model.WatchdogCritical);
        Assert.Contains(model.Rows,r=>r[1]=="Unreachable");Assert.Contains(model.Rows,r=>r[1]=="QUEUED");
    }

}
