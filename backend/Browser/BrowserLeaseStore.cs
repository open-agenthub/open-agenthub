using Npgsql;

namespace AgentHub.Api.Browser;

public interface IBrowserLeaseStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<bool> TryCreateAsync(BrowserLease lease, CancellationToken ct = default);
    Task<BrowserLease?> GetBySessionAsync(string sessionId, CancellationToken ct = default);
    Task<BrowserLease?> GetByLeaseAsync(string leaseId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, BrowserLease>> ListBySessionsAsync(
        IReadOnlyCollection<string> sessionIds, CancellationToken ct = default);
    Task SetRunningAsync(string leaseId, string podIp, CancellationToken ct = default);
    Task SetFailedAsync(string leaseId, string failureCode, CancellationToken ct = default);
    Task SetStoppingAsync(string leaseId, CancellationToken ct = default);
    Task DeleteAsync(string leaseId, CancellationToken ct = default);
}

public sealed class PostgresBrowserLeaseStore(IConfiguration configuration) : IBrowserLeaseStore
{
    private readonly NpgsqlDataSource _db = NpgsqlDataSource.Create(
        configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing."));

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS browser_leases (
                session_id   TEXT PRIMARY KEY REFERENCES sessions(id) ON DELETE CASCADE,
                lease_id     TEXT NOT NULL UNIQUE,
                token_hash   BYTEA NOT NULL,
                phase        TEXT NOT NULL,
                pod_ip       TEXT,
                failure_code TEXT,
                created_at   TIMESTAMPTZ NOT NULL,
                updated_at   TIMESTAMPTZ NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_browser_leases_lease_id ON browser_leases(lease_id);
            """;
        await using var command = _db.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TryCreateAsync(BrowserLease lease, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO browser_leases
                (session_id, lease_id, token_hash, phase, pod_ip, failure_code, created_at, updated_at)
            VALUES (@session, @lease, @hash, @phase, @pod, @failure, @created, @updated)
            ON CONFLICT (session_id) DO NOTHING;
            """;
        await using var command = _db.CreateCommand(sql);
        Bind(command, lease);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public Task<BrowserLease?> GetBySessionAsync(string sessionId, CancellationToken ct = default) =>
        QuerySingleAsync("session_id", sessionId, ct);

    public Task<BrowserLease?> GetByLeaseAsync(string leaseId, CancellationToken ct = default) =>
        QuerySingleAsync("lease_id", leaseId, ct);

    public async Task<IReadOnlyDictionary<string, BrowserLease>> ListBySessionsAsync(
        IReadOnlyCollection<string> sessionIds, CancellationToken ct = default)
    {
        if (sessionIds.Count == 0) return new Dictionary<string, BrowserLease>();
        await using var command = _db.CreateCommand(
            $"{SelectBase} WHERE session_id = ANY(@sessions)");
        command.Parameters.AddWithValue("sessions", sessionIds.ToArray());
        var result = new Dictionary<string, BrowserLease>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var lease = Map(reader);
            result[lease.SessionId] = lease;
        }
        return result;
    }

    public Task SetRunningAsync(string leaseId, string podIp, CancellationToken ct = default) =>
        UpdateAsync(leaseId, BrowserPhase.Running, podIp, null, ct);

    public Task SetFailedAsync(string leaseId, string failureCode, CancellationToken ct = default) =>
        UpdateAsync(leaseId, BrowserPhase.Failed, null, failureCode, ct);

    public Task SetStoppingAsync(string leaseId, CancellationToken ct = default) =>
        UpdateAsync(leaseId, BrowserPhase.Stopping, null, null, ct);

    public async Task DeleteAsync(string leaseId, CancellationToken ct = default)
    {
        await using var command = _db.CreateCommand("DELETE FROM browser_leases WHERE lease_id=@lease");
        command.Parameters.AddWithValue("lease", leaseId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private const string SelectBase =
        "SELECT session_id, lease_id, token_hash, phase, pod_ip, failure_code, created_at, updated_at FROM browser_leases";

    private async Task<BrowserLease?> QuerySingleAsync(
        string column, string value, CancellationToken ct)
    {
        await using var command = _db.CreateCommand($"{SelectBase} WHERE {column}=@value");
        command.Parameters.AddWithValue("value", value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    private async Task UpdateAsync(string leaseId, BrowserPhase phase, string? podIp,
        string? failureCode, CancellationToken ct)
    {
        const string sql = """
            UPDATE browser_leases
              SET phase=@phase, pod_ip=COALESCE(@pod, pod_ip), failure_code=@failure, updated_at=now()
              WHERE lease_id=@lease;
            """;
        await using var command = _db.CreateCommand(sql);
        command.Parameters.AddWithValue("phase", phase.ToString());
        command.Parameters.AddWithValue("pod", (object?)podIp ?? DBNull.Value);
        command.Parameters.AddWithValue("failure", (object?)failureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("lease", leaseId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(NpgsqlCommand command, BrowserLease lease)
    {
        command.Parameters.AddWithValue("session", lease.SessionId);
        command.Parameters.AddWithValue("lease", lease.LeaseId);
        command.Parameters.AddWithValue("hash", lease.TokenHash);
        command.Parameters.AddWithValue("phase", lease.Phase.ToString());
        command.Parameters.AddWithValue("pod", (object?)lease.PodIp ?? DBNull.Value);
        command.Parameters.AddWithValue("failure", (object?)lease.FailureCode ?? DBNull.Value);
        command.Parameters.AddWithValue("created", lease.CreatedAt);
        command.Parameters.AddWithValue("updated", lease.UpdatedAt);
    }

    private static BrowserLease Map(NpgsqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        (byte[])reader[2],
        Enum.Parse<BrowserPhase>(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetDateTime(6),
        reader.GetDateTime(7));
}
