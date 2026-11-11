using System.Text.Json;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Npgsql;

namespace AgentHub.Api.Admin;

/// <summary>A name the moved item had to take because the target already used its name.</summary>
/// <remarks><see cref="Scope"/> is the project id for a skill (unique per project scope), else empty.</remarks>
public sealed record HandoverRename(string Kind, string Name, string NewName, string Scope = "");

/// <summary>A session of the source owner, as far as the handover needs to know it.</summary>
public sealed record HandoverSession(string Id, string Mode);

/// <summary>
/// What a handover would do in the database right now. <see cref="Moves"/> is what the plan token
/// fixes: an apply whose recount differs was previewed on other data and is refused.
/// </summary>
public sealed record HandoverInventory(
    IReadOnlyDictionary<string, long> Moves,
    IReadOnlyDictionary<string, long> Stays,
    IReadOnlyList<HandoverRename> Renames,
    IReadOnlyList<HandoverSession> Sessions);

/// <summary>One handover as the audit records it. <see cref="State"/> is <c>committed</c> while the
/// cleanup after the database switch (source copies, rebuilt schedules) is outstanding.</summary>
public sealed record HandoverAuditEntry(
    long Id, string Admin, string From, string To, bool Credentials,
    IReadOnlyDictionary<string, long> Counts, IReadOnlyList<string> SessionIds,
    IReadOnlyList<string> ScheduledIds, string State, DateTime CreatedAt, DateTime? CompletedAt);

/// <summary>The counts changed between preview and apply.</summary>
public sealed class HandoverStaleException() : Exception("The data changed since the preview. Preview again.");

/// <summary>
/// The database half of handing the API-key owner's data to an OIDC user (docs/apikey-handover.md):
/// counts for the preview, and the switch of every owner column in one transaction together with
/// the audit row. Kubernetes secrets and object storage cannot join a Postgres transaction; the
/// service copies those before and removes the source copies after (OwnerHandoverService).
///
/// The table inventory follows <see cref="AccountPurgeStore"/>: a new per-user table belongs in
/// both. Tables an edition or an older release never created are skipped, because a reference to
/// a missing table fails the whole statement and with it the transaction.
/// </summary>
public sealed class OwnerHandoverStore
{
    public const string ProjectSuffix = " (API key)";
    public const string LibrarySuffix = "-api-key";

    // Inventory keys. The frontend labels them; the docs list what each covers.
    public const string Sessions = "sessions";
    public const string Projects = "projects";
    public const string Skills = "skills";
    public const string McpServers = "mcpServers";
    public const string Webhooks = "webhooks";
    public const string Files = "sessionFiles";
    public const string Usage = "usageRecords";
    public const string Messages = "sessionMessages";
    public const string Requests = "openRequests";
    public const string Shares = "shares";
    public const string ApiTokens = "apiTokens";
    public const string ChatThreads = "chatThreads";
    public const string UsageLimits = "usageLimits";

    private static readonly string[] KnownTables =
    [
        "sessions", "projects", "project_shares", "skills", "mcp_servers", "webhook_triggers",
        "session_files", "session_executions", "session_usage", "usage_monthly", "session_messages",
        "secret_requests", "permission_requests", "session_sidebar_order", "ephemeral_api_mcp",
        "session_transfer_tickets", "session_shares", "library_shares", "api_tokens",
        "chat_session_bindings", "chat_messages", "chat_pins", "slack_threads", "usage_limits",
    ];

    private readonly NpgsqlDataSource _db;
    private readonly Func<string, CancellationToken, Task>? _beforeStep;

    /// <param name="beforeStep">Called with the table name before each write of the switch; tests
    /// throw from it to prove that a failure part-way leaves nothing changed.</param>
    public OwnerHandoverStore(IConfiguration cfg, Func<string, CancellationToken, Task>? beforeStep = null)
    {
        _db = PostgresDataSources.Shared(cfg);
        _beforeStep = beforeStep;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await SchemaScript.ApplyAsync(_db, nameof(OwnerHandoverStore), """
            CREATE TABLE IF NOT EXISTS owner_handovers (
                id            BIGSERIAL PRIMARY KEY,
                admin         TEXT NOT NULL,
                from_owner    TEXT NOT NULL,
                to_owner      TEXT NOT NULL,
                credentials   BOOLEAN NOT NULL,
                counts        JSONB NOT NULL,
                session_ids   TEXT[] NOT NULL,
                scheduled_ids TEXT[] NOT NULL,
                state         TEXT NOT NULL CHECK (state IN ('committed', 'completed')),
                created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
                completed_at  TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_owner_handovers_pending ON owner_handovers (id) WHERE state = 'committed';
            """, ct);
    }

    public async Task<HandoverInventory> InventoryAsync(string from, string to, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await InventoryAsync(conn, null, from, to, ct);
    }

    /// <summary>
    /// Switches every owner column from <paramref name="from"/> to <paramref name="to"/> and records
    /// the audit row, all in one transaction, after checking that the counts are still
    /// <paramref name="expected"/>. Returns the audit entry.
    /// </summary>
    public async Task<HandoverAuditEntry> ApplyAsync(string admin, string from, string to, bool credentials,
        IReadOnlyDictionary<string, long> expected, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        // One handover at a time per instance: two concurrent ones on the same source would each
        // count the full set, and the second would then "move" nothing yet audit the counts.
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('owner-handover'))", conn, tx))
            await lockCmd.ExecuteNonQueryAsync(ct);

        var inventory = await InventoryAsync(conn, tx, from, to, ct);
        if (!SameCounts(inventory.Moves, expected)) throw new HandoverStaleException();

        var tables = await ExistingTablesAsync(conn, tx, ct);
        var ids = inventory.Sessions.Select(s => s.Id).ToArray();
        var step = new Step(conn, tx, tables, from, to, ids, _beforeStep);

        foreach (var rename in inventory.Renames)
        {
            var (table, scoped) = rename.Kind switch
            {
                Projects => ("projects", ""),
                Skills => ("skills", " AND COALESCE(project_id, '') = @scope"),
                _ => ("mcp_servers", ""),
            };
            await step.RunAsync(table, $"UPDATE {table} SET name = @n WHERE owner = @f AND name = @old{scoped}", c =>
            {
                c.Parameters.AddWithValue("n", rename.NewName);
                c.Parameters.AddWithValue("old", rename.Name);
                c.Parameters.AddWithValue("scope", rename.Scope);
            }, ct);
        }

        // Shares that would point the target at its own data now, removed before the owners change.
        await step.RunAsync("project_shares", """
            DELETE FROM project_shares WHERE subject_type = 'user' AND subject = @t
              AND project_id IN (SELECT id FROM projects WHERE owner = @f)
            """, ct: ct);
        await step.RunAsync("session_shares", "DELETE FROM session_shares WHERE recipient_owner = @t AND session_id = ANY(@ids)", ct: ct);
        await step.RunAsync("library_shares", """
            DELETE FROM library_shares WHERE subject_type = 'user' AND subject = @t AND (
                (item_type = 'skill' AND item_id IN (SELECT id FROM skills WHERE owner = @f)) OR
                (item_type = 'mcp' AND item_id IN (SELECT id FROM mcp_servers WHERE owner = @f)))
            """, ct: ct);
        // Somebody else's data shared with both names: the target keeps its own grant.
        await step.RunAsync("project_shares", """
            DELETE FROM project_shares s WHERE subject_type = 'user' AND subject = @f AND EXISTS (
                SELECT 1 FROM project_shares o WHERE o.project_id = s.project_id AND o.subject_type = 'user' AND o.subject = @t)
            """, ct: ct);
        await step.RunAsync("session_shares", """
            DELETE FROM session_shares s WHERE recipient_owner = @f AND EXISTS (
                SELECT 1 FROM session_shares o WHERE o.session_id = s.session_id AND o.recipient_owner = @t)
            """, ct: ct);
        await step.RunAsync("library_shares", """
            DELETE FROM library_shares s WHERE subject_type = 'user' AND subject = @f AND EXISTS (
                SELECT 1 FROM library_shares o WHERE o.item_type = s.item_type AND o.item_id = s.item_id
                  AND o.subject_type = 'user' AND o.subject = @t)
            """, ct: ct);

        await step.RunAsync("sessions", "UPDATE sessions SET owner = @t WHERE owner = @f", ct: ct);
        await step.RunAsync("projects", "UPDATE projects SET owner = @t WHERE owner = @f", ct: ct);
        await step.RunAsync("project_shares", "UPDATE project_shares SET subject = @t WHERE subject_type = 'user' AND subject = @f", ct: ct);
        await step.RunAsync("skills", "UPDATE skills SET owner = @t WHERE owner = @f", ct: ct);
        await step.RunAsync("mcp_servers", "UPDATE mcp_servers SET owner = @t WHERE owner = @f", ct: ct);
        await step.RunAsync("library_shares", "UPDATE library_shares SET created_by = @t WHERE created_by = @f", ct: ct);
        await step.RunAsync("library_shares", "UPDATE library_shares SET subject = @t WHERE subject_type = 'user' AND subject = @f", ct: ct);
        await step.RunAsync("session_shares", "UPDATE session_shares SET recipient_owner = @t WHERE recipient_owner = @f", ct: ct);
        await step.RunAsync("webhook_triggers", "UPDATE webhook_triggers SET owner = @t WHERE owner = @f", ct: ct);
        await step.RunAsync("secret_requests", "UPDATE secret_requests SET owner = @t WHERE owner = @f", ct: ct);
        await step.RunAsync("permission_requests", "UPDATE permission_requests SET owner = @t WHERE owner = @f", ct: ct);
        await step.RunAsync("session_messages", "UPDATE session_messages SET owner = @t WHERE owner = @f", ct: ct);
        await step.RunAsync("session_messages", "UPDATE session_messages SET from_owner = @t WHERE from_owner = @f", ct: ct);
        await step.RunAsync("session_sidebar_order", "UPDATE session_sidebar_order SET owner = @t WHERE owner = @f", ct: ct);
        await step.RunAsync("ephemeral_api_mcp", "UPDATE ephemeral_api_mcp SET owner = @t WHERE owner = @f", ct: ct);
        await step.RunAsync("session_usage", "UPDATE session_usage SET owner = @t WHERE owner = @f", ct: ct);
        // The month is the key, so the target's own months add up instead of colliding; the
        // monthly budget gate reads this table, and dropping the source's spend would reset it.
        await step.RunAsync("usage_monthly", """
            INSERT INTO usage_monthly (owner, month, cost_usd, estimated_cost_usd, updated_at)
            SELECT @t, month, cost_usd, estimated_cost_usd, now() FROM usage_monthly WHERE owner = @f
            ON CONFLICT (owner, month) DO UPDATE SET
                cost_usd = usage_monthly.cost_usd + EXCLUDED.cost_usd,
                estimated_cost_usd = usage_monthly.estimated_cost_usd + EXCLUDED.estimated_cost_usd,
                updated_at = now()
            """, ct: ct);
        await step.RunAsync("usage_monthly", "DELETE FROM usage_monthly WHERE owner = @f", ct: ct);

        // Object keys that carry the owner. The service copied the objects to the target's
        // prefixes before this transaction; the rows follow here so they never point at a prefix
        // that the cleanup afterwards removes.
        var fromFiles = $"sessions/{Uri.EscapeDataString(from)}/";
        var toFiles = $"sessions/{Uri.EscapeDataString(to)}/";
        await step.RunAsync("session_files", """
            UPDATE session_files SET owner = @t,
                storage_locator = CASE WHEN starts_with(storage_locator, @pf)
                    THEN @pt || substr(storage_locator, length(@pf) + 1) ELSE storage_locator END
            WHERE owner = @f
            """, c => { c.Parameters.AddWithValue("pf", fromFiles); c.Parameters.AddWithValue("pt", toFiles); }, ct);
        var fromKey = $"sessions/{KubernetesSessionService.OwnerKey(from)}/";
        var toKey = $"sessions/{KubernetesSessionService.OwnerKey(to)}/";
        await step.RunAsync("session_executions", """
            UPDATE session_executions SET
                transcript_key = CASE WHEN starts_with(transcript_key, @pf)
                    THEN @pt || substr(transcript_key, length(@pf) + 1) ELSE transcript_key END,
                state_key = CASE WHEN starts_with(state_key, @pf)
                    THEN @pt || substr(state_key, length(@pf) + 1) ELSE state_key END
            WHERE session_id = ANY(@ids)
            """, c => { c.Parameters.AddWithValue("pf", fromKey); c.Parameters.AddWithValue("pt", toKey); }, ct);

        // A take-local ticket is a bearer secret issued to the source; it does not travel.
        await step.RunAsync("session_transfer_tickets", "DELETE FROM session_transfer_tickets WHERE owner = @f", ct: ct);
        // Chat threads belong to the source's linked chat account. Moved with the session, a
        // reply from that account would act on the target's session; the target's next question
        // opens a thread in the target's own chat instead.
        await step.RunAsync("chat_messages", "DELETE FROM chat_messages WHERE session_id = ANY(@ids)", ct: ct);
        await step.RunAsync("chat_pins", "DELETE FROM chat_pins WHERE session_id = ANY(@ids)", ct: ct);
        await step.RunAsync("chat_session_bindings", "DELETE FROM chat_session_bindings WHERE session_id = ANY(@ids)", ct: ct);
        await step.RunAsync("slack_threads", "DELETE FROM slack_threads WHERE session_id = ANY(@ids)", ct: ct);

        var scheduled = inventory.Sessions.Where(s => s.Mode == "Scheduled").Select(s => s.Id).ToArray();
        await step.BeforeAsync("owner_handovers", ct);
        await using var audit = new NpgsqlCommand("""
            INSERT INTO owner_handovers (admin, from_owner, to_owner, credentials, counts, session_ids, scheduled_ids, state)
            VALUES (@a, @f, @t, @c, @counts::jsonb, @ids, @sched, 'committed')
            RETURNING id, created_at
            """, conn, tx);
        audit.Parameters.AddWithValue("a", admin);
        audit.Parameters.AddWithValue("f", from);
        audit.Parameters.AddWithValue("t", to);
        audit.Parameters.AddWithValue("c", credentials);
        audit.Parameters.AddWithValue("counts", JsonSerializer.Serialize(inventory.Moves));
        audit.Parameters.AddWithValue("ids", ids);
        audit.Parameters.AddWithValue("sched", scheduled);
        long id; DateTime at;
        await using (var r = await audit.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            id = r.GetInt64(0); at = r.GetDateTime(1);
        }
        await tx.CommitAsync(ct);
        return new HandoverAuditEntry(id, admin, from, to, credentials, inventory.Moves, ids, scheduled,
            "committed", at, null);
    }

    public async Task CompleteAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE owner_handovers SET state = 'completed', completed_at = now() WHERE id = @id AND state = 'committed'");
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The latest handovers, newest first; with <paramref name="pendingOnly"/> those whose
    /// cleanup is still outstanding.</summary>
    public async Task<IReadOnlyList<HandoverAuditEntry>> ListAsync(int limit, bool pendingOnly = false, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            SELECT id, admin, from_owner, to_owner, credentials, counts::text, session_ids, scheduled_ids,
                   state, created_at, completed_at
            FROM owner_handovers {(pendingOnly ? "WHERE state = 'committed'" : "")}
            ORDER BY id DESC LIMIT @l
            """);
        cmd.Parameters.AddWithValue("l", limit);
        var list = new List<HandoverAuditEntry>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new HandoverAuditEntry(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetBoolean(4), JsonSerializer.Deserialize<Dictionary<string, long>>(r.GetString(5)) ?? new(),
                r.GetFieldValue<string[]>(6), r.GetFieldValue<string[]>(7), r.GetString(8),
                r.GetDateTime(9), r.IsDBNull(10) ? null : r.GetDateTime(10)));
        return list;
    }

    private static bool SameCounts(IReadOnlyDictionary<string, long> a, IReadOnlyDictionary<string, long> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    private static async Task<HandoverInventory> InventoryAsync(NpgsqlConnection conn, NpgsqlTransaction? tx,
        string from, string to, CancellationToken ct)
    {
        var tables = await ExistingTablesAsync(conn, tx, ct);
        async Task<long> Count(string table, string sql)
        {
            if (!tables.Contains(table)) return 0;
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("f", from);
            cmd.Parameters.AddWithValue("t", to);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        var sessions = new List<HandoverSession>();
        if (tables.Contains("sessions"))
        {
            await using var cmd = new NpgsqlCommand("SELECT id, mode FROM sessions WHERE owner = @f ORDER BY id", conn, tx);
            cmd.Parameters.AddWithValue("f", from);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) sessions.Add(new HandoverSession(r.GetString(0), r.GetString(1)));
        }

        var moves = new Dictionary<string, long>
        {
            [Sessions] = sessions.Count,
            [Projects] = await Count("projects", "SELECT count(*) FROM projects WHERE owner = @f"),
            [Skills] = await Count("skills", "SELECT count(*) FROM skills WHERE owner = @f"),
            [McpServers] = await Count("mcp_servers", "SELECT count(*) FROM mcp_servers WHERE owner = @f"),
            [Webhooks] = await Count("webhook_triggers", "SELECT count(*) FROM webhook_triggers WHERE owner = @f"),
            [Files] = await Count("session_files", "SELECT count(*) FROM session_files WHERE owner = @f"),
            [Usage] = await Count("session_usage", "SELECT count(*) FROM session_usage WHERE owner = @f")
                + await Count("usage_monthly", "SELECT count(*) FROM usage_monthly WHERE owner = @f"),
            [Messages] = await Count("session_messages", "SELECT count(*) FROM session_messages WHERE owner = @f OR from_owner = @f"),
            [Requests] = await Count("secret_requests", "SELECT count(*) FROM secret_requests WHERE owner = @f")
                + await Count("permission_requests", "SELECT count(*) FROM permission_requests WHERE owner = @f"),
            [Shares] = await Count("project_shares", "SELECT count(*) FROM project_shares WHERE subject_type = 'user' AND subject = @f")
                + await Count("session_shares", "SELECT count(*) FROM session_shares WHERE recipient_owner = @f")
                + await Count("library_shares", "SELECT count(*) FROM library_shares WHERE created_by = @f OR (subject_type = 'user' AND subject = @f)"),
        };
        var ids = sessions.Select(s => s.Id).ToArray();
        var stays = new Dictionary<string, long>
        {
            [ApiTokens] = await Count("api_tokens", "SELECT count(*) FROM api_tokens WHERE owner = @f"),
            [UsageLimits] = await Count("usage_limits", "SELECT count(*) FROM usage_limits WHERE scope = 'user' AND target = @f"),
        };
        long chats = 0;
        foreach (var table in new[] { "chat_session_bindings", "slack_threads" })
        {
            if (!tables.Contains(table)) continue;
            await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table} WHERE session_id = ANY(@ids)", conn, tx);
            cmd.Parameters.AddWithValue("ids", ids);
            chats += Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        stays[ChatThreads] = chats;

        var renames = new List<HandoverRename>();
        if (tables.Contains("projects"))
            renames.AddRange(await RenamesAsync(conn, tx, Projects, "projects", from, to,
                (name, n) => Suffixed(name, n == 1 ? ProjectSuffix : $" (API key {n})", int.MaxValue), ignoreCase: true, ct));
        if (tables.Contains("skills"))
            renames.AddRange(await RenamesAsync(conn, tx, Skills, "skills", from, to,
                (name, n) => Suffixed(name, n == 1 ? LibrarySuffix : $"{LibrarySuffix}-{n}", Library.LibraryValidation.MaxNameLength),
                ignoreCase: false, ct));
        if (tables.Contains("mcp_servers"))
            renames.AddRange(await RenamesAsync(conn, tx, McpServers, "mcp_servers", from, to,
                (name, n) => Suffixed(name, n == 1 ? LibrarySuffix : $"{LibrarySuffix}-{n}", Library.LibraryValidation.MaxNameLength),
                ignoreCase: true, ct));
        return new HandoverInventory(moves, stays, renames, sessions);
    }

    /// <summary>
    /// The source's names that the target already uses within the same uniqueness scope, each with
    /// the first suffixed name free on both sides, compared the way the table's unique index does
    /// (case-insensitive for projects and MCP servers, per project scope for skills).
    /// </summary>
    private static async Task<List<HandoverRename>> RenamesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx,
        string kind, string table, string from, string to, Func<string, int, string> rename,
        bool ignoreCase, CancellationToken ct)
    {
        var withScope = table == "skills";
        var rows = new List<(string Owner, string Name, string Scope)>();
        await using (var cmd = new NpgsqlCommand(
            $"SELECT owner, name, {(withScope ? "COALESCE(project_id, '')" : "''")} FROM {table} WHERE owner = @f OR owner = @t ORDER BY name",
            conn, tx))
        {
            cmd.Parameters.AddWithValue("f", from);
            cmd.Parameters.AddWithValue("t", to);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) rows.Add((r.GetString(0), r.GetString(1), r.GetString(2)));
        }
        string Key(string scope, string name) => scope + "/" + (ignoreCase ? name.ToLowerInvariant() : name);
        var taken = rows.Select(x => Key(x.Scope, x.Name)).ToHashSet(StringComparer.Ordinal);
        var targetNames = rows.Where(x => x.Owner == to).Select(x => Key(x.Scope, x.Name)).ToHashSet(StringComparer.Ordinal);
        var result = new List<HandoverRename>();
        foreach (var row in rows.Where(x => x.Owner == from && targetNames.Contains(Key(x.Scope, x.Name))))
        {
            string candidate;
            var n = 1;
            do candidate = rename(row.Name, n++); while (taken.Contains(Key(row.Scope, candidate)));
            taken.Add(Key(row.Scope, candidate));
            result.Add(new HandoverRename(kind, row.Name, candidate, row.Scope));
        }
        return result;
    }

    private static string Suffixed(string name, string suffix, int max) =>
        name.Length + suffix.Length <= max ? name + suffix : name[..(max - suffix.Length)].TrimEnd('-') + suffix;

    private static async Task<HashSet<string>> ExistingTablesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT t FROM unnest(@names) AS t WHERE to_regclass(t) IS NOT NULL", conn, tx);
        cmd.Parameters.AddWithValue("names", KnownTables);
        var set = new HashSet<string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>One guarded write inside the transaction. @f, @t and @ids are always bound.</summary>
    private sealed class Step(NpgsqlConnection conn, NpgsqlTransaction tx, HashSet<string> tables,
        string from, string to, string[] ids, Func<string, CancellationToken, Task>? before)
    {
        public async Task BeforeAsync(string table, CancellationToken ct)
        {
            if (before is not null) await before(table, ct);
        }

        public async Task RunAsync(string table, string sql, Action<NpgsqlCommand>? bind = null, CancellationToken ct = default)
        {
            if (!tables.Contains(table)) return;
            await BeforeAsync(table, ct);
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("f", from);
            cmd.Parameters.AddWithValue("t", to);
            cmd.Parameters.AddWithValue("ids", ids);
            bind?.Invoke(cmd);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
