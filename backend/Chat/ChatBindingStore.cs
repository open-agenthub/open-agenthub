using Npgsql;

namespace AgentHub.Api.Chat;

/// <summary>A session's conversation on one chat platform ("telegram" | "signal").</summary>
public sealed record ChatBinding(
    string Platform, string SessionId, string Owner, string ChatId,
    string? ThreadId,      // Telegram forum topic id; null for DMs/Signal
    string? StatusRef,     // message id/timestamp of the "working…" indicator
    bool Active);          // the chat's current default session (plain replies go here). Callers must upsert
                           // with Active=false and flip via SetActiveAsync — UpsertAsync does not clear other rows' flags.

/// <summary>Maps sessions to their chat conversations and outgoing messages to sessions (reply routing).</summary>
public sealed class ChatBindingStore
{
    private const string Columns = "platform, session_id, owner, chat_id, thread_id, status_ref, active";
    private readonly NpgsqlDataSource _db;

    public ChatBindingStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
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
        await using var cmd = _db.CreateCommand(
            "UPDATE chat_session_bindings SET active = (session_id = @sid) WHERE platform = @p AND chat_id = @c");
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

    /// <summary>Prunes reply-routing rows older than 30 days. Called daily by the poll services.</summary>
    public async Task PruneMessagesAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            "DELETE FROM chat_messages WHERE created_at < now() - interval '30 days'");
        await cmd.ExecuteNonQueryAsync(ct);
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
