using AgentHub.Api.Persistence;
using Npgsql;

namespace AgentHub.Api.Chat;

/// <summary>A session's conversation on one chat platform ("telegram" | "signal").</summary>
public sealed record ChatBinding(
    string Platform, string SessionId, string Owner, string ChatId,
    string? ThreadId,      // Telegram forum topic id; null for DMs/Signal
    string? StatusRef,     // message id/timestamp of the "working…" indicator
    bool Active);          // the chat's current default session (plain replies go here). Callers must upsert
                           // with Active=false and flip via SetActiveAsync — UpsertAsync does not clear other rows' flags.

/// <summary>
/// What a chat still knows about a deleted session: where its conversation was and whether
/// it was the chat's active session. Kept so the chat's next message into it gets "deleted"
/// as its answer, and so an expiry notice sent after the delete still finds the thread.
/// </summary>
public sealed record DeletedChatSession(
    string Platform, string SessionId, string Owner, string ChatId, string? ThreadId, string? Title, bool WasActive);

/// <summary>Drops a deleted session's chat state. Implemented by <see cref="ChatBindingStore"/>.</summary>
public interface IChatSessionCleanup
{
    Task RemoveSessionAsync(string sessionId, string? title, CancellationToken ct = default);
}

/// <summary>Maps sessions to their chat conversations and outgoing messages to sessions (reply routing).</summary>
public sealed class ChatBindingStore : IChatSessionCleanup
{
    /// <summary>How long reply-routing rows and deleted-session markers are kept.</summary>
    private const string Retention = "interval '30 days'";
    private const string DeletedColumns = "platform, session_id, owner, chat_id, thread_id, title, was_active";
    private const string Columns = "platform, session_id, owner, chat_id, thread_id, status_ref, active";
    private readonly NpgsqlDataSource _db;

    public ChatBindingStore(IConfiguration cfg)
    {
        _db = PostgresDataSources.Shared(cfg);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS chat_session_bindings (
                platform    TEXT NOT NULL,
                session_id  TEXT NOT NULL,
                owner       TEXT NOT NULL,
                chat_id     TEXT NOT NULL,
                thread_id   TEXT,
                status_ref  TEXT,
                active      BOOLEAN NOT NULL DEFAULT FALSE,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (platform, session_id)
            );
            CREATE INDEX IF NOT EXISTS idx_chat_bindings_chat ON chat_session_bindings(platform, chat_id);
            CREATE TABLE IF NOT EXISTS chat_messages (
                platform    TEXT NOT NULL,
                chat_id     TEXT NOT NULL,
                message_ref TEXT NOT NULL,
                session_id  TEXT NOT NULL,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (platform, chat_id, message_ref)
            );
            CREATE TABLE IF NOT EXISTS chat_deleted_sessions (
                platform    TEXT NOT NULL,
                session_id  TEXT NOT NULL,
                owner       TEXT NOT NULL,
                chat_id     TEXT NOT NULL,
                thread_id   TEXT,
                title       TEXT,
                was_active  BOOLEAN NOT NULL DEFAULT FALSE,
                deleted_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (platform, session_id)
            );
            CREATE INDEX IF NOT EXISTS idx_chat_deleted_chat ON chat_deleted_sessions(platform, chat_id);
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(ChatBinding b, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO chat_session_bindings (platform, session_id, owner, chat_id, thread_id, status_ref, active)
            VALUES (@platform, @sid, @owner, @chat, @thread, @status, @active)
            ON CONFLICT (platform, session_id) DO UPDATE SET
                owner = EXCLUDED.owner, chat_id = EXCLUDED.chat_id, thread_id = EXCLUDED.thread_id,
                status_ref = EXCLUDED.status_ref, active = EXCLUDED.active;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("platform", b.Platform);
        cmd.Parameters.AddWithValue("sid", b.SessionId);
        cmd.Parameters.AddWithValue("owner", b.Owner);
        cmd.Parameters.AddWithValue("chat", b.ChatId);
        cmd.Parameters.AddWithValue("thread", (object?)b.ThreadId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", (object?)b.StatusRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("active", b.Active);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ChatBinding?> GetAsync(string platform, string sessionId, CancellationToken ct = default)
        => await QueryOne("WHERE platform = @p AND session_id = @a", ct, ("p", platform), ("a", sessionId));

    /// <summary>Binding for a Telegram forum topic (platform + chat + thread).</summary>
    public async Task<ChatBinding?> GetByThreadAsync(string platform, string chatId, string threadId, CancellationToken ct = default)
        => await QueryOne("WHERE platform = @p AND chat_id = @a AND thread_id = @b LIMIT 1", ct, ("p", platform), ("a", chatId), ("b", threadId));

    /// <summary>The chat's active session (plain messages without reply go here).</summary>
    public async Task<ChatBinding?> GetActiveAsync(string platform, string chatId, CancellationToken ct = default)
        => await QueryOne("WHERE platform = @p AND chat_id = @a AND active LIMIT 1", ct, ("p", platform), ("a", chatId));

    /// <summary>Marks one session active per chat (clears the flag on all others of that chat).</summary>
    public async Task SetActiveAsync(string platform, string chatId, string sessionId, CancellationToken ct = default)
    {
        // A new choice also retires the "your active session was deleted" answer, which would
        // otherwise contradict the session the chat now talks to.
        await using var cmd = _db.CreateCommand("""
            UPDATE chat_session_bindings SET active = (session_id = @sid) WHERE platform = @p AND chat_id = @c;
            UPDATE chat_deleted_sessions SET was_active = FALSE WHERE platform = @p AND chat_id = @c AND was_active;
            """);
        cmd.Parameters.AddWithValue("sid", sessionId);
        cmd.Parameters.AddWithValue("p", platform);
        cmd.Parameters.AddWithValue("c", chatId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>All bindings of a chat (for !sessions), most recently created session first. The caller enriches with live session data.</summary>
    public async Task<IReadOnlyList<ChatBinding>> ListByChatAsync(string platform, string chatId, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT {Columns} FROM chat_session_bindings WHERE platform = @p AND chat_id = @c ORDER BY created_at DESC");
        cmd.Parameters.AddWithValue("p", platform);
        cmd.Parameters.AddWithValue("c", chatId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ChatBinding>();
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task SetStatusRefAsync(string platform, string sessionId, string? statusRef, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "UPDATE chat_session_bindings SET status_ref = @s WHERE platform = @p AND session_id = @sid");
        cmd.Parameters.AddWithValue("s", (object?)statusRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("p", platform);
        cmd.Parameters.AddWithValue("sid", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordMessageAsync(string platform, string chatId, string messageRef, string sessionId, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO chat_messages (platform, chat_id, message_ref, session_id)
            VALUES (@p, @c, @m, @sid)
            ON CONFLICT DO NOTHING;
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("p", platform);
        cmd.Parameters.AddWithValue("c", chatId);
        cmd.Parameters.AddWithValue("m", messageRef);
        cmd.Parameters.AddWithValue("sid", sessionId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetSessionByMessageAsync(string platform, string chatId, string messageRef, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "SELECT session_id FROM chat_messages WHERE platform = @p AND chat_id = @c AND message_ref = @m");
        cmd.Parameters.AddWithValue("p", platform);
        cmd.Parameters.AddWithValue("c", chatId);
        cmd.Parameters.AddWithValue("m", messageRef);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    /// <summary>
    /// Forgets a deleted session on every platform: its bindings (and with them the chat's
    /// active marker) and its reply-routing rows. A marker per binding remembers the chat and
    /// topic. Idempotent, so a delete that is retried after a later step failed runs it again.
    /// </summary>
    public async Task RemoveSessionAsync(string sessionId, string? title, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand($"""
            INSERT INTO chat_deleted_sessions ({DeletedColumns})
            SELECT platform, session_id, owner, chat_id, thread_id, @title, active
            FROM chat_session_bindings WHERE session_id = @sid
            ON CONFLICT (platform, session_id) DO UPDATE SET
                owner = EXCLUDED.owner, chat_id = EXCLUDED.chat_id, thread_id = EXCLUDED.thread_id,
                title = EXCLUDED.title, was_active = EXCLUDED.was_active, deleted_at = now();
            DELETE FROM chat_session_bindings WHERE session_id = @sid;
            DELETE FROM chat_messages WHERE session_id = @sid;
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("sid", sessionId);
            cmd.Parameters.AddWithValue("title", (object?)title ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Daily cleanup, run by the poll services (also once at their start, which is what cleans
    /// an install that deleted sessions before deletes removed their bindings): reply-routing
    /// rows and deleted-session markers past the retention, and the chat state of every
    /// session that no longer exists. Returns the number of stale bindings removed.
    /// </summary>
    public async Task<int> PruneAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        int removed;
        // The orphans become markers too: on an upgraded install the chat's active session is
        // typically one of them, and its next message deserves the same answer as after a delete.
        await using (var cmd = new NpgsqlCommand($"""
            INSERT INTO chat_deleted_sessions ({DeletedColumns})
            SELECT platform, session_id, owner, chat_id, thread_id, NULL, active
            FROM chat_session_bindings b WHERE NOT EXISTS (SELECT 1 FROM sessions s WHERE s.id = b.session_id)
            ON CONFLICT (platform, session_id) DO NOTHING
            """, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = new NpgsqlCommand(
            "DELETE FROM chat_session_bindings b WHERE NOT EXISTS (SELECT 1 FROM sessions s WHERE s.id = b.session_id)", conn, tx))
            removed = await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = new NpgsqlCommand($"""
            DELETE FROM chat_messages m
            WHERE m.created_at < now() - {Retention}
               OR NOT EXISTS (SELECT 1 FROM sessions s WHERE s.id = m.session_id);
            DELETE FROM chat_deleted_sessions WHERE deleted_at < now() - {Retention};
            """, conn, tx))
            await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return removed;
    }

    /// <summary>The marker a deleted session left on this platform (expiry notices use it).</summary>
    public Task<DeletedChatSession?> GetDeletedAsync(string platform, string sessionId, CancellationToken ct = default)
        => QueryDeleted("WHERE platform = @p AND session_id = @a", ct, ("p", platform), ("a", sessionId));

    /// <summary>The deleted session that was this chat's active one, until another is chosen.</summary>
    public Task<DeletedChatSession?> GetDeletedActiveAsync(string platform, string chatId, CancellationToken ct = default)
        => QueryDeleted("WHERE platform = @p AND chat_id = @a AND was_active ORDER BY deleted_at DESC LIMIT 1", ct, ("p", platform), ("a", chatId));

    /// <summary>The deleted session whose forum topic this is.</summary>
    public Task<DeletedChatSession?> GetDeletedByThreadAsync(string platform, string chatId, string threadId, CancellationToken ct = default)
        => QueryDeleted("WHERE platform = @p AND chat_id = @a AND thread_id = @b ORDER BY deleted_at DESC LIMIT 1", ct, ("p", platform), ("a", chatId), ("b", threadId));

    private async Task<DeletedChatSession?> QueryDeleted(string where, CancellationToken ct, params (string Name, string Value)[] ps)
    {
        await using var cmd = _db.CreateCommand($"SELECT {DeletedColumns} FROM chat_deleted_sessions {where}");
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetBoolean(6));
    }

    private async Task<ChatBinding?> QueryOne(string where, CancellationToken ct, params (string Name, string Value)[] ps)
    {
        await using var cmd = _db.CreateCommand($"SELECT {Columns} FROM chat_session_bindings {where}");
        foreach (var (name, value) in ps) cmd.Parameters.AddWithValue(name, value);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static ChatBinding Map(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.GetBoolean(6));
}
