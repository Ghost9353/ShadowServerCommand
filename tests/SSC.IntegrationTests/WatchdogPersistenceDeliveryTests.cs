using System.Net;
using System.Text.Json;
using SSC.Core;
using SSC.Infrastructure;
using Xunit;
namespace SSC.IntegrationTests;
public sealed class WatchdogPersistenceDeliveryTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"ssc-watchdog-"+Guid.NewGuid());
    private WatchdogRepository Open() { var r=new WatchdogRepository(Path.Combine(_directory,"watchdog.db"));r.Initialize();return r; }
    private sealed class Secrets : ISecretStore { public string Read(string _) => "https://discord.com/api/webhooks/123/test-token-not-real"; }
    private sealed class Transport : HttpMessageHandler
    {
        public List<string> Bodies { get; }=[];
        public bool Offline { get; set; }
        public HttpStatusCode Status { get; set; }=HttpStatusCode.NoContent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            if(Offline)throw new HttpRequestException("simulated internet loss");
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(Status) { Content=new StringContent(Status==HttpStatusCode.TooManyRequests?"{\"retry_after\":120}":"") };
        }
    }
    private static ProbeSample Probe(DateTimeOffset now,bool online) => new(now,online,online?10:null,online,online,online?new(now,1000,true,true,"verified identity",15,10000,2000,"test"):null,"SIMULATED OUTAGE TEST");
    [Fact] public async Task SimulatedOutageAndRecoveryDeliverExactlyTwoQueuedEventsAcrossRestart()
    {
        var repo=Open();var state=repo.Load();var detector=new WatchdogDetector(new());var start=DateTimeOffset.UtcNow.AddMinutes(-10);
        for(var i=0;i<3;i++){var p=Probe(start.AddSeconds(i*15),false);repo.Commit(state,p,detector.Apply(state,p));}
        repo=Open();state=repo.Load();Assert.Equal(ComputerCondition.Unreachable,state.Computer);
        using var handler=new Transport();using var http=new HttpClient(handler);var delivery=new DiscordDelivery(repo,http,new Secrets(),new());
        Assert.True(await delivery.PumpAsync(DateTimeOffset.UtcNow,CancellationToken.None));Assert.False(await delivery.PumpAsync(DateTimeOffset.UtcNow,CancellationToken.None));
        for(var i=3;i<6;i++){var p=Probe(start.AddSeconds(i*15),true);repo.Commit(state,p,detector.Apply(state,p,i==3));}
        Assert.True(await delivery.PumpAsync(DateTimeOffset.UtcNow,CancellationToken.None));Assert.Equal(2,handler.Bodies.Count);
        Assert.Contains("UNREACHABLE",handler.Bodies[0]);Assert.Contains("RESTORED",handler.Bodies[1]);
        using var json=JsonDocument.Parse(handler.Bodies[0]);Assert.Empty(json.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        Assert.Equal(0,repo.DeliverySummary().Pending);Assert.True(Assert.Single(repo.Load().Incidents).ObservationInterrupted);
    }
    [Fact] public async Task InternetLossPersistsQueueAndBackoffThenDeliversAfterRestart()
    {
        var repo=Open();var now=DateTimeOffset.UtcNow;repo.Enqueue(new(Guid.NewGuid(),now,"test","safe test"));
        using var handler=new Transport { Offline=true };using var http=new HttpClient(handler);var delivery=new DiscordDelivery(repo,http,new Secrets(),new());
        Assert.False(await delivery.PumpAsync(now,CancellationToken.None));Assert.Null(repo.NextAlert(now));
        repo=Open();handler.Offline=false;delivery=new(repo,http,new Secrets(),new());
        Assert.True(await delivery.PumpAsync(now.AddSeconds(6),CancellationToken.None));Assert.Equal(0,repo.DeliverySummary().Pending);
    }
    [Fact] public async Task RateLimitRespectsRetryAfterAndInvalidWebhookIsHeld()
    {
        var repo=Open();var now=DateTimeOffset.UtcNow;repo.Enqueue(new(Guid.NewGuid(),now,"test","safe test"));
        using var handler=new Transport { Status=HttpStatusCode.TooManyRequests };using var http=new HttpClient(handler);var delivery=new DiscordDelivery(repo,http,new Secrets(),new());
        Assert.False(await delivery.PumpAsync(now,CancellationToken.None));Assert.Null(repo.NextAlert(now.AddSeconds(119)));
        handler.Status=HttpStatusCode.NotFound;Assert.False(await delivery.PumpAsync(now.AddSeconds(121),CancellationToken.None));
        Assert.Equal(1,repo.DeliverySummary().Held);repo.RetryHeld(now.AddMinutes(3));Assert.NotNull(repo.NextAlert(now.AddMinutes(3)));
    }
    [Fact] public void StateAndOutboxDeduplicationAreDurable()
    {
        var repo=Open();var now=DateTimeOffset.UtcNow;var alert=new PendingAlert(Guid.NewGuid(),now,"test","dedup");
        repo.Enqueue(alert);repo.Enqueue(alert);Assert.Equal(1,Open().DeliverySummary().Pending);
    }
    public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
