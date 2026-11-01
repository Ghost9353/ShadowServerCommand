using SSC.Core;
using Xunit;
namespace SSC.UnitTests;
public sealed class WatchdogDetectionTests
{
    private static readonly DateTimeOffset Epoch=new(2026,10,31,12,0,0,TimeSpan.Zero);
    public static ProbeSample Sample(int tick,bool heartbeat=true,bool network=true,bool port=true,bool? process=true) => new(Epoch.AddSeconds(tick*15),heartbeat,heartbeat?12:null,network,port,
        heartbeat?new(Epoch.AddSeconds(tick*15),1000,true,process,"test identity",20,10000,2000,"test"):null,"simulated probe evidence");
    [Fact] public void NormalOperationHasNoAlerts()
    {
        var s=new WatchdogState();var d=new WatchdogDetector(new());
        for(var i=0;i<10;i++)Assert.Empty(d.Apply(s,Sample(i)));
        Assert.Equal(ComputerCondition.Online,s.Computer);Assert.Equal(MinecraftCondition.Running,s.Minecraft);
    }
    [Theory] [InlineData(false,false,ComputerCondition.Unreachable)] [InlineData(true,false,ComputerCondition.AgentUnavailable)] [InlineData(false,true,ComputerCondition.AgentUnavailable)]
    public void ThreeFailedHeartbeatsUseIndependentNetworkEvidence(bool network,bool port,ComputerCondition expected)
    {
        var s=new WatchdogState();var d=new WatchdogDetector(new());
        Assert.Empty(d.Apply(s,Sample(0,false,network,port,null)));Assert.Empty(d.Apply(s,Sample(1,false,network,port,null)));
        var alert=Assert.Single(d.Apply(s,Sample(2,false,network,port,null)));Assert.Contains("UNKNOWN",alert.Message);
        Assert.Equal(expected,s.Computer);Assert.Equal(Epoch,Assert.Single(s.Incidents).StartedAt);
        for(var i=3;i<20;i++)Assert.Empty(d.Apply(s,Sample(i,false,network,port,null)));
    }
    [Theory] [InlineData(false,false,MinecraftCondition.ProcessStopped)] [InlineData(true,false,MinecraftCondition.PotentiallyUnresponsive)] [InlineData(null,false,MinecraftCondition.PortUnavailable)]
    public void MinecraftFailureIsSeparateFromHost(bool? process,bool port,MinecraftCondition expected)
    {
        var s=new WatchdogState();var d=new WatchdogDetector(new());
        for(var i=0;i<3;i++)d.Apply(s,Sample(i));
        for(var i=3;i<6;i++)d.Apply(s,Sample(i,true,true,port,process));
        Assert.Equal(ComputerCondition.Online,s.Computer);Assert.Equal(expected,s.Minecraft);Assert.NotNull(s.MinecraftIncident);
    }
    [Fact] public void RecoveryRequiresThreeSuccessesAndRetainsMeasuredDuration()
    {
        var s=new WatchdogState();var d=new WatchdogDetector(new());
        for(var i=0;i<3;i++)d.Apply(s,Sample(i,false,false,false));
        Assert.Empty(d.Apply(s,Sample(3)));Assert.Empty(d.Apply(s,Sample(4)));
        var alert=Assert.Single(d.Apply(s,Sample(5)));Assert.Contains("RECOVERED",alert.Kind);
        Assert.Equal(Epoch.AddSeconds(75),Assert.Single(s.Incidents).RecoveredAt);
        Assert.Equal(ComputerCondition.Online,s.Computer);
    }
    [Fact] public void RestartRecordsGapAndDoesNotInferContinuousObservation()
    {
        var s=new WatchdogState();var d=new WatchdogDetector(new());
        for(var i=0;i<3;i++)d.Apply(s,Sample(i,false,false,false));
        d.Apply(s,Sample(100),true);Assert.Single(s.Gaps);Assert.True(s.Incidents[0].ObservationInterrupted);
        Assert.NotNull(s.ComputerIncident);d.Apply(s,Sample(101));
        var recovery=Assert.Single(d.Apply(s,Sample(102)));Assert.Contains("includes unobserved time",recovery.Message);
    }
    [Fact] public void SleepBreaksConsecutiveFailureAndCrashEvidence()
    {
        var s=new WatchdogState();var d=new WatchdogDetector(new());
        d.Apply(s,Sample(0));d.Apply(s,Sample(1,false,false,false));
        d.Apply(s,Sample(100,false,false,false));Assert.Equal(1,s.Failures);Assert.Single(s.Gaps);
        d.Apply(s,Sample(101,true,true,false,false));Assert.Empty(s.CrashTimes);
    }
    [Fact] public void ObservedRepeatedStopsGenerateOneBurstAlert()
    {
        var s=new WatchdogState();var d=new WatchdogDetector(new() { FailureThreshold=1,RecoveryThreshold=1,CrashBurstCount=3 });
        var alerts=new List<PendingAlert>();
        for(var i=0;i<10;i++)alerts.AddRange(d.Apply(s,Sample(i,true,true,i%2==0,i%2==0)));
        Assert.Single(alerts,a=>a.Kind=="REPEATED MINECRAFT STOPS");
    }
    [Fact] public void FailedStatusReplyWithOpenPortIsPotentiallyUnresponsive()
    {
        var state=new WatchdogState();var detector=new WatchdogDetector(new());
        for(var i=0;i<3;i++)detector.Apply(state,Sample(i) with { MinecraftResponsive=false });
        Assert.Equal(MinecraftCondition.PotentiallyUnresponsive,state.Minecraft);
    }
    [Fact] public void AgentToHostEscalationIsSentOnlyOnceEvenWithNetworkFlapping()
    {
        var s=new WatchdogState();var d=new WatchdogDetector(new() { FailureThreshold=1 });
        Assert.Single(d.Apply(s,Sample(0,false,true,false)));Assert.Single(d.Apply(s,Sample(1,false,false,false)));
        Assert.Empty(d.Apply(s,Sample(2,false,true,false)));Assert.Empty(d.Apply(s,Sample(3,false,false,false)));
    }
}
