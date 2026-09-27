using Npgsql;

namespace AgentHub.Api.Mcp;

/// <summary>An MCP client that registered itself via RFC 7591 dynamic client registration.</summary>
public sealed class McpClientRegistration
{
    public required string ClientId { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<string> RedirectUris { get; init; }
    /// <summary>Users who approved this client on the consent screen.</summary>
    public required IReadOnlyCollection<string> ApprovedBy { get; init; }
}

/// <summary>
/// Persists dynamically registered MCP clients and their per-user approvals.
///
/// The OpenIddict application store itself is in-memory and is rebuilt from here at startup.
/// Registrations have to outlive a restart: a client that loses its registration gets an
/// <c>invalid_client</c> on its next silent reconnect, which surfaces to the user as a broken
/// connector rather than as a prompt to sign in again.
///
/// Approvals are stored per (client, user) because registration is anonymous. Being registered
/// must not imply permission to act for anyone — see the consent step in
/// <see cref="McpAuthorizationController"/>.
/// </summary>
public sealed class McpClientStore
{
    private readonly NpgsqlDataSource _db;

    public McpClientStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS mcp_clients (
                client_id     TEXT PRIMARY KEY,
                display_name  TEXT NOT NULL,
                redirect_uris TEXT[] NOT NULL,
                created_at    TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS mcp_client_approvals (
                client_id   TEXT NOT NULL REFERENCES mcp_clients(client_id) ON DELETE CASCADE,
                owner       TEXT NOT NULL,
                approved_at TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (client_id, owner)
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddAsync(string clientId, string displayName, IReadOnlyList<string> redirectUris,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO mcp_clients (client_id, display_name, redirect_uris)
            VALUES (@id, @name, @uris)
            ON CONFLICT (client_id) DO NOTHING;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", clientId);
        cmd.Parameters.AddWithValue("name", displayName);
        cmd.Parameters.AddWithValue("uris", redirectUris.ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<McpClientRegistration>> ListAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT c.client_id, c.display_name, c.redirect_uris,
                   COALESCE(ARRAY_AGG(a.owner) FILTER (WHERE a.owner IS NOT NULL), '{}') AS approvals
            FROM mcp_clients c
            LEFT JOIN mcp_client_approvals a ON a.client_id = c.client_id
            GROUP BY c.client_id, c.display_name, c.redirect_uris;
            """;
        await using var cmd = _db.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var result = new List<McpClientRegistration>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(new McpClientRegistration
            {
                ClientId = reader.GetString(0),
                DisplayName = reader.GetString(1),
                RedirectUris = reader.GetFieldValue<string[]>(2),
                ApprovedBy = reader.GetFieldValue<string[]>(3)
            });
        }
        return result;
    }

    /// <summary>True when the user already approved this client on the consent screen.</summary>
    public async Task<bool> IsApprovedByAsync(string? clientId, string owner, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(clientId)) return false;
        const string sql = "SELECT 1 FROM mcp_client_approvals WHERE client_id=@id AND owner=@owner;";
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", clientId);
        cmd.Parameters.AddWithValue("owner", owner);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>Records an approval. Returns false when the client is not registered.</summary>
    public async Task<bool> ApproveAsync(string clientId, string owner, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO mcp_client_approvals (client_id, owner)
            SELECT @id, @owner WHERE EXISTS (SELECT 1 FROM mcp_clients WHERE client_id=@id)
            ON CONFLICT (client_id, owner) DO NOTHING;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", clientId);
        cmd.Parameters.AddWithValue("owner", owner);
        // 0 rows means either "already approved" or "unknown client"; distinguish explicitly so
        // an unknown client cannot be silently treated as approved.
        var affected = await cmd.ExecuteNonQueryAsync(ct);
        if (affected > 0) return true;
        return await ExistsAsync(clientId, ct);
    }

    private async Task<bool> ExistsAsync(string clientId, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT 1 FROM mcp_clients WHERE client_id=@id;");
        cmd.Parameters.AddWithValue("id", clientId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
