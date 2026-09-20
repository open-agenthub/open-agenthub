using Npgsql;

namespace AgentHub.Api.Network;

/// <summary>An approved (and applied) port opening of a session.</summary>
public sealed record PortGrant(
    string SessionId, PortDirection Direction, int Port, string Protocol, DateTimeOffset CreatedAt);

public interface IPortGrantStore
{
    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>Records the grant; idempotent (re-approving the same port is a no-op).</summary>
    Task AddAsync(string sessionId, PortDirection direction, int port, string protocol, CancellationToken ct = default);
    Task<bool> ExistsAsync(string sessionId, PortDirection direction, int port, string protocol, CancellationToken ct = default);
    Task<IReadOnlyList<PortGrant>> ListAsync(string sessionId, CancellationToken ct = default);
    Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Cross-replica record of applied port grants. Postgres (not memory) so the
/// grant survives backend restarts and every replica answers port_list the same.
/// </summary>
public sealed class PortGrantStore : IPortGrantStore
{
    private readonly NpgsqlDataSource _db;

    public PortGrantStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS network_port_grants (
                session_id  TEXT NOT NULL,
                direction   TEXT NOT NULL,
                port        INT  NOT NULL,
                protocol    TEXT NOT NULL,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (session_id, direction, port, protocol)
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddAsync(string sessionId, PortDirection direction, int port, string protocol, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            INSERT INTO network_port_grants (session_id, direction, port, protocol)
            VALUES (@s, @d, @p, @proto) ON CONFLICT DO NOTHING
            """);
        AddKey(cmd, sessionId, direction, port, protocol);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ExistsAsync(string sessionId, PortDirection direction, int port, string protocol, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT 1 FROM network_port_grants WHERE session_id=@s AND direction=@d AND port=@p AND protocol=@proto");
        AddKey(cmd, sessionId, direction, port, protocol);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<IReadOnlyList<PortGrant>> ListAsync(string sessionId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            SELECT session_id, direction, port, protocol, created_at
            FROM network_port_grants WHERE session_id=@s ORDER BY created_at
            """);
        cmd.Parameters.AddWithValue("s", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PortGrant>();
        while (await r.ReadAsync(ct))
        {
            if (!PortRequestKey.TryParseDirection(r.GetString(1), out var direction)) continue;
            list.Add(new PortGrant(r.GetString(0), direction, r.GetInt32(2), r.GetString(3),
                r.GetFieldValue<DateTime>(4)));
        }
        return list;
    }

    public async Task DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM network_port_grants WHERE session_id=@s");
        cmd.Parameters.AddWithValue("s", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddKey(NpgsqlCommand cmd, string sessionId, PortDirection direction, int port, string protocol)
    {
        cmd.Parameters.AddWithValue("s", sessionId);
        cmd.Parameters.AddWithValue("d", PortRequestKey.WireName(direction));
        cmd.Parameters.AddWithValue("p", port);
        cmd.Parameters.AddWithValue("proto", protocol.ToUpperInvariant());
    }
}
