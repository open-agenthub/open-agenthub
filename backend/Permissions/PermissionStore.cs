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
    /// <summary>null = pending; otherwise "allow" | "allowAlways" | "deny" | "expired".</summary>
    public string? Decision { get; set; }
    // Where the chat prompt was posted (platform + conversation + message ref),
    // for updating it once decided/expired.
    public string? Platform { get; set; }
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
            ALTER TABLE permission_requests ADD COLUMN IF NOT EXISTS platform TEXT;
            -- Prompt-message lookup for reaction/quote-based deciders (Signal).
            CREATE INDEX IF NOT EXISTS idx_permreq_prompt ON permission_requests(platform, channel, message_ts);
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

    /// <summary>Records where the chat prompt was posted (platform + conversation + message ref),
    /// so it can be updated once decided/expired.</summary>
    public async Task SetPromptMessageAsync(string id, string platform, string channel, string messageRef, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE permission_requests SET platform=@p, channel=@c, message_ts=@m WHERE id=@id");
        cmd.Parameters.AddWithValue("p", platform);
        cmd.Parameters.AddWithValue("c", channel);
        cmd.Parameters.AddWithValue("m", messageRef);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // When a sessionId is given, the query additionally requires the request to belong to
    // that session — the internal endpoints pass it so one session's token cannot touch
    // another session's requests. The Slack click path only has the request id (null).
    public async Task<string?> GetDecisionAsync(string id, string? sessionId = null, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT decision FROM permission_requests WHERE id=@id{SessionFilter(sessionId)}");
        cmd.Parameters.AddWithValue("id", id);
        AddSessionParam(cmd, sessionId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Returns the full request row, or null if unknown (or not in the given session).</summary>
    public async Task<PermissionRequest?> GetAsync(string id, string? sessionId = null, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            SELECT id, session_id, owner, tool, summary, decision, channel, message_ts, platform
            FROM permission_requests WHERE id=@id{SessionFilter(sessionId)}
            """);
        cmd.Parameters.AddWithValue("id", id);
        AddSessionParam(cmd, sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return Map(r);
    }

    /// <summary>The request whose chat prompt is the given message (platform + conversation +
    /// message ref), or null. Used by reaction/quote-based deciders (Signal) that only know
    /// which message was reacted to, not the request id.</summary>
    public async Task<PermissionRequest?> GetByPromptMessageAsync(string platform, string channel, string messageRef, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            SELECT id, session_id, owner, tool, summary, decision, channel, message_ts, platform
            FROM permission_requests WHERE platform=@p AND channel=@c AND message_ts=@m
            """);
        cmd.Parameters.AddWithValue("p", platform);
        cmd.Parameters.AddWithValue("c", channel);
        cmd.Parameters.AddWithValue("m", messageRef);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    /// <summary>Sets the decision if still pending (and, when given, belonging to the session);
    /// returns the request (for updating the chat prompt) or null.</summary>
    public async Task<PermissionRequest?> ResolveAsync(string id, string decision, string? sessionId = null, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            UPDATE permission_requests SET decision=@d, decided_at=now()
            WHERE id=@id AND decision IS NULL{SessionFilter(sessionId)}
            RETURNING id, session_id, owner, tool, summary, decision, channel, message_ts, platform
            """);
        cmd.Parameters.AddWithValue("d", decision);
        cmd.Parameters.AddWithValue("id", id);
        AddSessionParam(cmd, sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return Map(r);
    }

    /// <summary>Tool name of the newest still-undecided request of a session, or null.</summary>
    public async Task<string?> GetPendingBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT tool FROM permission_requests WHERE session_id=@s AND decision IS NULL ORDER BY created_at DESC LIMIT 1");
        cmd.Parameters.AddWithValue("s", sessionId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// Expires all still-pending requests older than <paramref name="olderThan"/> and
    /// returns them (full rows, so the sweeper can defuse their chat prompts). Safety
    /// net for hooks that died without calling the /expire endpoint.
    /// </summary>
    public async Task<IReadOnlyList<PermissionRequest>> ExpireStaleAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("""
            UPDATE permission_requests SET decision='expired', decided_at=now()
            WHERE decision IS NULL AND created_at < now() - @age
            RETURNING id, session_id, owner, tool, summary, decision, channel, message_ts, platform
            """);
        cmd.Parameters.Add(new NpgsqlParameter("age", NpgsqlTypes.NpgsqlDbType.Interval) { Value = olderThan });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<PermissionRequest>();
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    private static string SessionFilter(string? sessionId) => sessionId is null ? "" : " AND session_id=@sid";

    private static void AddSessionParam(NpgsqlCommand cmd, string? sessionId)
    {
        if (sessionId is not null) cmd.Parameters.AddWithValue("sid", sessionId);
    }

    private static PermissionRequest Map(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0), SessionId = r.GetString(1), Owner = r.GetString(2),
        Tool = r.GetString(3), Summary = r.IsDBNull(4) ? null : r.GetString(4),
        Decision = r.IsDBNull(5) ? null : r.GetString(5),
        Channel = r.IsDBNull(6) ? null : r.GetString(6),
        MessageTs = r.IsDBNull(7) ? null : r.GetString(7),
        Platform = r.IsDBNull(8) ? null : r.GetString(8)
    };

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
