using System.Text.Json;
using Microsoft.Data.Sqlite;
using SSC.Core;
namespace SSC.Infrastructure;
public sealed record AlertDelivery(PendingAlert Alert, int Attempts);
public sealed class WatchdogRepository(string path)
{
    private readonly string _connection = new SqliteConnectionStringBuilder { DataSource=path, DefaultTimeout=10 }.ToString();
    private SqliteConnection Open() { var c=new SqliteConnection(_connection);c.Open();return c; }
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var c=Open();
        using(var pragma=c.CreateCommand()) { pragma.CommandText="PRAGMA journal_mode=WAL";pragma.ExecuteNonQuery(); }
        using var tx=c.BeginTransaction();using var cmd=c.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="CREATE TABLE IF NOT EXISTS watchdog_schema(version INTEGER PRIMARY KEY);SELECT COALESCE(MAX(version),0) FROM watchdog_schema";
        var version=Convert.ToInt32(cmd.ExecuteScalar());
        if(version>1)throw new InvalidDataException("Unsupported watchdog database schema.");
        if(version==0)
        {
            cmd.CommandText="""
                CREATE TABLE watchdog_state(id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
                CREATE TABLE watchdog_samples(id INTEGER PRIMARY KEY, observed_utc TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE TABLE watchdog_incidents(id TEXT PRIMARY KEY, started_utc TEXT NOT NULL, recovered_utc TEXT, payload TEXT NOT NULL);
                CREATE TABLE monitoring_gaps(id INTEGER PRIMARY KEY, from_utc TEXT NOT NULL, until_utc TEXT NOT NULL, payload TEXT NOT NULL);
                CREATE UNIQUE INDEX ix_gap ON monitoring_gaps(from_utc,until_utc);
                CREATE TABLE alert_outbox(id TEXT PRIMARY KEY, created_utc TEXT NOT NULL, payload TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, next_utc TEXT, delivered_utc TEXT, status TEXT NOT NULL DEFAULT 'Pending');
                INSERT INTO watchdog_schema VALUES(1);
                """;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    public WatchdogState Load()
    {
        using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT payload FROM watchdog_state WHERE id=1";
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<WatchdogState>(json) ?? throw new InvalidDataException("Invalid watchdog state") : new();
    }
    public void Commit(WatchdogState state, ProbeSample sample, IReadOnlyList<PendingAlert> alerts)
    {
        using var c=Open();using var tx=c.BeginTransaction();
        Execute(c,tx,"INSERT INTO watchdog_state VALUES(1,$p) ON CONFLICT(id) DO UPDATE SET payload=$p",("$p",JsonSerializer.Serialize(state)));
        Execute(c,tx,"INSERT INTO watchdog_samples(observed_utc,payload) VALUES($t,$p)",("$t",sample.ObservedAt.ToString("O")),("$p",JsonSerializer.Serialize(sample)));
        foreach(var i in state.Incidents) Execute(c,tx,"INSERT INTO watchdog_incidents VALUES($id,$start,$end,$p) ON CONFLICT(id) DO UPDATE SET recovered_utc=$end,payload=$p",("$id",i.Id.ToString()),("$start",i.StartedAt.ToString("O")),("$end",i.RecoveredAt?.ToString("O")),("$p",JsonSerializer.Serialize(i)));
        foreach(var gap in state.Gaps) Execute(c,tx,"INSERT OR IGNORE INTO monitoring_gaps(from_utc,until_utc,payload) VALUES($from,$until,$p)",("$from",gap.From.ToString("O")),("$until",gap.Until.ToString("O")),("$p",JsonSerializer.Serialize(gap)));
        foreach(var alert in alerts) Enqueue(c,tx,alert);
        tx.Commit();
    }
    public void Enqueue(PendingAlert alert) { using var c=Open();using var tx=c.BeginTransaction();Enqueue(c,tx,alert);tx.Commit(); }
    private static void Enqueue(SqliteConnection c, SqliteTransaction tx, PendingAlert alert) => Execute(c,tx,"INSERT OR IGNORE INTO alert_outbox(id,created_utc,payload,next_utc) VALUES($id,$t,$p,$t)",("$id",alert.Id.ToString()),("$t",alert.CreatedAt.ToString("O")),("$p",JsonSerializer.Serialize(alert)));
    public AlertDelivery? NextAlert(DateTimeOffset now)
    {
        using var c=Open();using var cmd=c.CreateCommand();
        // FIFO: never deliver recovery ahead of an earlier pending outage, even during backoff.
        cmd.CommandText="SELECT payload,attempts,next_utc FROM alert_outbox WHERE delivered_utc IS NULL AND status!='Held' ORDER BY created_utc,rowid LIMIT 1";
        using var r=cmd.ExecuteReader();
        if(!r.Read() || r.IsDBNull(2) || DateTimeOffset.Parse(r.GetString(2))>now) return null;
        return new(JsonSerializer.Deserialize<PendingAlert>(r.GetString(0))!,r.GetInt32(1));
    }
    public void DeliveryResult(Guid id,int attempts,string status,DateTimeOffset? next,DateTimeOffset? delivered)
    {
        using var c=Open();using var tx=c.BeginTransaction();
        Execute(c,tx,"UPDATE alert_outbox SET attempts=$n,status=$s,next_utc=$next,delivered_utc=$delivered WHERE id=$id",("$n",attempts),("$s",status),("$next",next?.ToString("O")),("$delivered",delivered?.ToString("O")),("$id",id.ToString()));tx.Commit();
    }
    public (int Pending,int Held,DateTimeOffset? LastDelivered,string Status) DeliverySummary()
    {
        using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FILTER(WHERE delivered_utc IS NULL),COUNT(*) FILTER(WHERE status='Held'),MAX(delivered_utc) FROM alert_outbox";
        using var r=cmd.ExecuteReader();r.Read();var pending=r.GetInt32(0);var held=r.GetInt32(1);var last=r.IsDBNull(2)?(DateTimeOffset?)null:DateTimeOffset.Parse(r.GetString(2));
        return(pending,held,last,held>0?"ACTION REQUIRED — held alerts":pending>0?"QUEUED / RETRY PENDING":"QUEUE EMPTY");
    }
    public bool IsDelivered(Guid id)
    {
        using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT delivered_utc IS NOT NULL FROM alert_outbox WHERE id=$id";cmd.Parameters.AddWithValue("$id",id.ToString());
        return Convert.ToInt32(cmd.ExecuteScalar())==1;
    }
    public void RetryHeld(DateTimeOffset now)
    { using var c=Open();using var tx=c.BeginTransaction();Execute(c,tx,"UPDATE alert_outbox SET status='Pending',next_utc=$now,attempts=0 WHERE status='Held'",("$now",now.ToString("O")));tx.Commit(); }
    private static void Execute(SqliteConnection c,SqliteTransaction tx,string sql,params (string Key,object? Value)[] args)
    { using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;foreach(var a in args)cmd.Parameters.AddWithValue(a.Key,a.Value??DBNull.Value);cmd.ExecuteNonQuery(); }
}
