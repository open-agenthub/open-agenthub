using Npgsql;

namespace AgentHub.Api.Persistence;

public interface IAccountPurgeStore
{
    /// <summary>Ids of every session the owner has — collected before the purge so
    /// session-scoped rows can be removed even after the session rows are gone.</summary>
    Task<IReadOnlyList<string>> ListSessionIdsAsync(string owner, CancellationToken ct = default);

    /// <summary>Deletes every database row tied to the owner. Returns the ids of the
    /// owner's library skills so their S3 content can be removed as well.</summary>
    Task<IReadOnlyList<string>> PurgeAsync(string owner, IReadOnlyCollection<string> sessionIds, CancellationToken ct = default);
}

/// <summary>
/// GDPR account purge: the single inventory of every Postgres table that stores rows
/// keyed to a user — directly by owner column or indirectly via their sessions, skills
/// or MCP servers. New per-user tables MUST be added here. Each table is deleted with
/// its own statement, guarded by to_regclass so a deployment where an (EE) store never
/// created its table does not fail the purge.
/// </summary>
public sealed class AccountPurgeStore : IAccountPurgeStore
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<AccountPurgeStore> _log;

    public AccountPurgeStore(IConfiguration cfg, ILogger<AccountPurgeStore> log)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
        _log = log;
    }

    public async Task<IReadOnlyList<string>> ListSessionIdsAsync(string owner, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT id FROM sessions WHERE owner = @o");
        cmd.Parameters.AddWithValue("o", owner);
        var ids = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return ids;
    }

    public async Task<IReadOnlyList<string>> PurgeAsync(string owner, IReadOnlyCollection<string> sessionIds, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        var sessions = sessionIds.ToArray();
        var skills = await ColumnAsync(conn, "skills", "SELECT id FROM skills WHERE owner = @o", owner, ct);
        var mcpServers = await ColumnAsync(conn, "mcp_servers", "SELECT id FROM mcp_servers WHERE owner = @o", owner, ct);

        // Owner-keyed tables.
        await DeleteAsync(conn, "api_tokens", "owner = @o", owner, sessions, ct);
        await DeleteAsync(conn, "projects", "owner = @o", owner, sessions, ct);
        await DeleteAsync(conn, "chat_link_codes", "owner = @o", owner, sessions, ct);
        await DeleteAsync(conn, "chat_session_bindings", "owner = @o", owner, sessions, ct);
        await DeleteAsync(conn, "permission_requests", "owner = @o", owner, sessions, ct);
        await DeleteAsync(conn, "session_usage", "owner = @o OR session_id = ANY(@ids)", owner, sessions, ct);
        await DeleteAsync(conn, "usage_monthly", "owner = @o", owner, sessions, ct);
        await DeleteAsync(conn, "mcp_servers", "owner = @o", owner, sessions, ct);

        // Session-keyed tables — by the ids collected up front, so rows survive neither
        // a preceding per-session delete nor its absence.
        await DeleteAsync(conn, "chat_messages", "session_id = ANY(@ids)", owner, sessions, ct);
        await DeleteAsync(conn, "permission_rules", "session_id = ANY(@ids)", owner, sessions, ct);
        await DeleteAsync(conn, "session_file_presentations", "session_id = ANY(@ids)", owner, sessions, ct);
        await DeleteAsync(conn, "session_files", "session_id = ANY(@ids)", owner, sessions, ct);
        await DeleteAsync(conn, "ephemeral_api_mcp", "session_id = ANY(@ids)", owner, sessions, ct);
        await DeleteAsync(conn, "browser_leases", "session_id = ANY(@ids)", owner, sessions, ct);

        // Library skills incl. version history and embeddings.
        if (skills.Count > 0)
        {
            await DeleteAsync(conn, "skill_embeddings", "skill_id = ANY(@ids)", owner, skills.ToArray(), ct);
            await DeleteAsync(conn, "skill_files", "skill_id = ANY(@ids)", owner, skills.ToArray(), ct);
            await DeleteAsync(conn, "skill_versions", "skill_id = ANY(@ids)", owner, skills.ToArray(), ct);
        }
        await DeleteAsync(conn, "skills", "owner = @o", owner, sessions, ct);

        // Enterprise tables (guarded like all others — they may not exist on CE-only DBs).
        await DeleteAsync(conn, "slack_threads", "owner = @o", owner, sessions, ct);
        await DeleteAsync(conn, "user_groups", "owner = @o", owner, sessions, ct);
        await DeleteAsync(conn, "usage_limits", "scope = 'user' AND target = @o", owner, sessions, ct);
        await DeleteAsync(conn, "session_shares", "recipient_owner = @o OR session_id = ANY(@ids)", owner, sessions, ct);
        await DeleteAsync(conn, "session_share_links", "session_id = ANY(@ids)", owner, sessions, ct);
        await DeleteAsync(conn, "session_mcp_policies", "session_id = ANY(@ids)", owner, sessions, ct);
        var shared = skills.Concat(mcpServers).ToArray();
        await DeleteAsync(conn, "library_shares",
            "created_by = @o OR (subject_type = 'user' AND subject = @o) OR item_id = ANY(@ids)", owner, shared, ct);

        // The sessions and the account row go last: everything above may still need them
        // as a reference point when the purge is retried after a partial failure.
        await DeleteAsync(conn, "sessions", "owner = @o", owner, sessions, ct);
        await DeleteAsync(conn, "app_users", "owner = @o", owner, sessions, ct);

        _log.LogInformation("Purged account data ({Sessions} sessions, {Skills} skills)", sessions.Length, skills.Count);
        return skills;
    }

    private static async Task<List<string>> ColumnAsync(NpgsqlConnection conn, string table, string sql, string owner, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, table, ct)) return new List<string>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("o", owner);
        var values = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) values.Add(reader.GetString(0));
        return values;
    }

    /// <summary>DELETE guarded by table existence. @o = owner, @ids = the id array —
    /// bound unconditionally so the where clause can use either or both.</summary>
    private async Task DeleteAsync(NpgsqlConnection conn, string table, string where,
        string owner, string[] ids, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, table, ct)) return;
        await using var cmd = new NpgsqlCommand($"DELETE FROM {table} WHERE {where}", conn);
        cmd.Parameters.AddWithValue("o", owner);
        cmd.Parameters.AddWithValue("ids", ids);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0) _log.LogDebug("Account purge removed {Rows} rows from {Table}", rows, table);
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("t", table);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
