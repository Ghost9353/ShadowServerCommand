using System.Text.Json;
using Microsoft.Data.Sqlite;
using SSC.Core;
namespace SSC.Infrastructure;
public sealed class SqliteMetricStore(string databasePath) : IMetricStore
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, ForeignKeys = true, DefaultTimeout = 10 }.ToString();
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        Directory.CreateDirectory(directory!);
        await using var connection = await OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        { pragma.CommandText = "PRAGMA journal_mode=WAL;"; await pragma.ExecuteNonQueryAsync(cancellationToken); }
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_utc TEXT NOT NULL); SELECT COALESCE(MAX(version),0) FROM schema_migrations;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (version > 1) throw new InvalidOperationException("Database schema is newer than this application; use a compatible version.");
        if (version < 1)
        {
            command.CommandText = """
                CREATE TABLE telemetry_snapshots(id INTEGER PRIMARY KEY, timestamp_utc TEXT NOT NULL, origin TEXT NOT NULL, payload_json TEXT NOT NULL);
                CREATE INDEX ix_telemetry_time ON telemetry_snapshots(timestamp_utc);
                CREATE TABLE incidents(id TEXT PRIMARY KEY, timestamp_utc TEXT NOT NULL, severity TEXT NOT NULL, source TEXT NOT NULL, summary TEXT NOT NULL, origin TEXT NOT NULL);
                CREATE TABLE player_sessions(id TEXT PRIMARY KEY, player_id TEXT NOT NULL, joined_utc TEXT NOT NULL, left_utc TEXT);
                CREATE TABLE connection_attempts(id INTEGER PRIMARY KEY, timestamp_utc TEXT NOT NULL, address TEXT NOT NULL, outcome TEXT NOT NULL, source TEXT NOT NULL);
                CREATE TABLE audit_events(id TEXT PRIMARY KEY, timestamp_utc TEXT NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL, outcome TEXT NOT NULL, correlation_id TEXT NOT NULL);
                INSERT INTO schema_migrations(version,applied_utc) VALUES(1,$now);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
    public async Task AppendAsync(TelemetrySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO telemetry_snapshots(timestamp_utc,origin,payload_json) VALUES($time,$origin,$json)";
        command.Parameters.AddWithValue("$time", snapshot.Timestamp.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$origin", snapshot.Origin.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<TelemetrySnapshot>> ReadRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM telemetry_snapshots ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<TelemetrySnapshot>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(JsonSerializer.Deserialize<TelemetrySnapshot>(reader.GetString(0)) ?? throw new InvalidDataException("Invalid snapshot payload"));
        return result;
    }
}
