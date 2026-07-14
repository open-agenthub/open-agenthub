using Npgsql;

namespace AgentHub.Api.Permissions;

/// <summary>A pending (or resolved) tool-permission request raised by a session.</summary>
public sealed class PermissionRequest
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required string Owner { get; init; }
    public required string Tool { get; init; }
    public string? Summary { get; init; }
    /// <summary>null = pending; otherwise "allow" | "deny".</summary>
    public string? Decision { get; set; }
    // Where the Slack prompt was posted (for updating it once decided).
    public string? Channel { get; set; }
    public string? MessageTs { get; set; }
}

/// <summary>
/// Cross-replica store for tool-permission requests. The agent's PreToolUse hook polls
/// for a decision here; the Slack interaction handler (possibly on another replica)
/// writes it — hence Postgres rather than in-memory.
/// </summary>
public sealed class PermissionStore
{
    private readonly NpgsqlDataSource _db;

    public PermissionStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS permission_requests (
                id          TEXT PRIMARY KEY,
                session_id  TEXT NOT NULL,
                owner       TEXT NOT NULL,
                tool        TEXT NOT NULL,
                summary     TEXT,
                decision    TEXT,
                channel     TEXT,
                message_ts  TEXT,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                decided_at  TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_permreq_created ON permission_requests(created_at);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateAsync(PermissionRequest r, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "INSERT INTO permission_requests (id, session_id, owner, tool, summary) VALUES (@id,@s,@o,@t,@sum)");
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("s", r.SessionId);
        cmd.Parameters.AddWithValue("o", r.Owner);
        cmd.Parameters.AddWithValue("t", r.Tool);
        cmd.Parameters.AddWithValue("sum", (object?)r.Summary ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Records where the Slack prompt was posted, so it can be updated on decision.</summary>
    public async Task SetSlackMessageAsync(string id, string channel, string messageTs, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE permission_requests SET channel=@c, message_ts=@m WHERE id=@id");
        cmd.Parameters.AddWithValue("c", channel);
        cmd.Parameters.AddWithValue("m", messageTs);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetDecisionAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT decision FROM permission_requests WHERE id=@id");
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Sets the decision if still pending; returns the request (for updating Slack) or null.</summary>
    public async Task<PermissionRequest?> ResolveAsync(string id, string decision, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            UPDATE permission_requests SET decision=@d, decided_at=now()
            WHERE id=@id AND decision IS NULL
            RETURNING id, session_id, owner, tool, summary, decision, channel, message_ts
            """);
        cmd.Parameters.AddWithValue("d", decision);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new PermissionRequest
        {
            Id = r.GetString(0), SessionId = r.GetString(1), Owner = r.GetString(2),
            Tool = r.GetString(3), Summary = r.IsDBNull(4) ? null : r.GetString(4),
            Decision = r.IsDBNull(5) ? null : r.GetString(5),
            Channel = r.IsDBNull(6) ? null : r.GetString(6),
            MessageTs = r.IsDBNull(7) ? null : r.GetString(7)
        };
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM permission_requests WHERE id=@id");
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Relays a permission request to an out-of-band approver (Slack). Implemented in the
/// enterprise Slack module; returns true if a prompt was actually posted (i.e. the user
/// has a Slack target), false otherwise so the caller falls back to the normal flow.
/// </summary>
public interface IPermissionNotifier
{
    Task<bool> PostAsync(PermissionRequest request, CancellationToken ct = default);
}
