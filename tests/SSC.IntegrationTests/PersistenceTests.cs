using Microsoft.Data.Sqlite;
using SSC.Infrastructure;
using Xunit;
namespace SSC.IntegrationTests;
public sealed class PersistenceTests : IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"ssc-tests-"+Guid.NewGuid());
    private string Database => Path.Combine(_directory,"metrics.db");
    [Fact] public async Task MigrationIsIdempotentAndDataSurvivesNewStore()
    {
        var store=new SqliteMetricStore(Database);
        await store.InitializeAsync(); await store.InitializeAsync();
        var snapshot=await new SimulatedTelemetrySource().ReadAsync(CancellationToken.None);
        await store.AppendAsync(snapshot);
        var reopened=new SqliteMetricStore(Database); await reopened.InitializeAsync();
        var result=Assert.Single(await reopened.ReadRecentAsync(10));
        Assert.Equal(snapshot.Timestamp,result.Timestamp); Assert.Equal(snapshot.Origin,result.Origin);
        Assert.Equal(snapshot.Metrics.Count,result.Metrics.Count);
        await using var connection=new SqliteConnection("Data Source="+Database); await connection.OpenAsync();
        await using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM schema_migrations";
        Assert.Equal(1L,await command.ExecuteScalarAsync());
    }
    [Fact] public async Task FutureSchemaIsRejectedWithoutDeletingData()
    {
        var store=new SqliteMetricStore(Database);await store.InitializeAsync();
        await using(var connection=new SqliteConnection("Data Source="+Database))
        {
            await connection.OpenAsync(); await using var command=connection.CreateCommand();
            command.CommandText="INSERT INTO schema_migrations VALUES(2,'future')";await command.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.InitializeAsync());
    }
    [Fact] public async Task InvalidReadLimitIsRejected()
    { await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>new SqliteMetricStore(Database).ReadRecentAsync(0)); }
    public void Dispose() { SqliteConnection.ClearAllPools(); if(Directory.Exists(_directory)) Directory.Delete(_directory,true); }
}
