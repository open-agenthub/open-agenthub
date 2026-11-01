using AgentHub.Api.Models;
using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>
/// A message/task one project agent sent to another. <see cref="FromSessionId"/> is null
/// for messages sent from outside a session (remote API token, the owner in the web app).
/// <see cref="DeliveredAt"/> is set when the message reached the agent — taken from the inbox,
/// written into its terminal or chat, or handed to its Claude Code mod — and
/// <see cref="DeliveredVia"/> says which (<see cref="MessageDeliveryVia"/>).
/// </summary>
public sealed class SessionMessageRecord
{
    public required string Id { get; init; }
    public string? ProjectId { get; init; }
    public string? FromSessionId { get; init; }
    public required string ToSessionId { get; init; }
    /// <summary>Owner of both sessions — denormalized so reads never need a join.</summary>
    public required string Owner { get; init; }
    public required string Body { get; init; }
    /// <summary>Pushed straight into the running agent instead of waiting for its inbox poll.</summary>
    public bool Priority { get; init; }
    /// <summary>Stops the agent's current work before the message is delivered. Implies priority.</summary>
    public bool Interrupt { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
    public string? DeliveredVia { get; set; }
}

public interface ISessionMessageStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task AddAsync(SessionMessageRecord message, CancellationToken ct = default);
    /// <summary>Atomically marks up to <paramref name="limit"/> undelivered messages of the
    /// session as delivered and returns them (oldest first). <paramref name="via"/> is
    /// <see cref="MessageDeliveryVia.Inbox"/> for <c>agent_inbox</c> and
    /// <see cref="MessageDeliveryVia.NextTurn"/> when the pod takes them to hand over as a turn.</summary>
    Task<IReadOnlyList<SessionMessageRecord>> TakeUndeliveredAsync(string toSessionId, int limit, string via,
        CancellationToken ct = default);
    /// <summary>Recent messages of the session (newest first), delivered or not — read-only view for the UI.</summary>
    Task<IReadOnlyList<SessionMessageRecord>> ListRecentAsync(string toSessionId, int limit, CancellationToken ct = default);
    /// <summary>Owner-bound history uses a timestamp/id keyset so new arrivals do not shift later pages.</summary>
    Task<IReadOnlyList<SessionMessageRecord>> ListPageAsync(string owner, string toSessionId,
        MessagePageCursor? before, int limit, CancellationToken ct = default)
        => throw new NotSupportedException();
    /// <summary>Marks one message delivered by a push (<see cref="MessageDeliveryVia.Injected"/> or
    /// <see cref="MessageDeliveryVia.Mod"/>), so the inbox never hands it out a second time.
    /// A message the inbox already took keeps its first delivery.</summary>
    Task MarkDeliveredAsync(string id, string via, CancellationToken ct = default);
}

public sealed class PostgresSessionMessageStore : ISessionMessageStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresSessionMessageStore(IConfiguration cfg)
    {
        _db = PostgresDataSources.Shared(cfg);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS session_messages (
                id              TEXT PRIMARY KEY,
                project_id      TEXT,
                from_session_id TEXT,
                to_session_id   TEXT NOT NULL,
                owner           TEXT NOT NULL,
                body            TEXT NOT NULL,
                created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
                delivered_at    TIMESTAMPTZ
            );
            CREATE INDEX IF NOT EXISTS idx_session_messages_inbox
                ON session_messages(to_session_id, delivered_at);
            CREATE INDEX IF NOT EXISTS idx_session_messages_history
                ON session_messages(owner, to_session_id, created_at DESC, id COLLATE "C" DESC);
            ALTER TABLE session_messages ADD COLUMN IF NOT EXISTS priority BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE session_messages ADD COLUMN IF NOT EXISTS interrupt BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE session_messages ADD COLUMN IF NOT EXISTS delivered_via TEXT;
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddAsync(SessionMessageRecord m, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO session_messages (id, project_id, from_session_id, to_session_id, owner, body, created_at, delivered_at, priority, interrupt, delivered_via)
            VALUES (@id, @project, @from, @to, @owner, @body, @created, @delivered, @priority, @interrupt, @via);
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", m.Id);
        cmd.Parameters.AddWithValue("project", (object?)m.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from", (object?)m.FromSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to", m.ToSessionId);
        cmd.Parameters.AddWithValue("owner", m.Owner);
        cmd.Parameters.AddWithValue("body", m.Body);
        cmd.Parameters.AddWithValue("created", m.CreatedAt);
        cmd.Parameters.AddWithValue("delivered", (object?)m.DeliveredAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("priority", m.Priority);
        cmd.Parameters.AddWithValue("interrupt", m.Interrupt);
        cmd.Parameters.AddWithValue("via", (object?)m.DeliveredVia ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SessionMessageRecord>> TakeUndeliveredAsync(string toSessionId, int limit, string via,
        CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE session_messages SET delivered_at = now(), delivered_via = @via
            WHERE id IN (
                SELECT id FROM session_messages
                WHERE to_session_id = @to AND delivered_at IS NULL
                ORDER BY created_at
                LIMIT @limit
                FOR UPDATE SKIP LOCKED
            )
            RETURNING {Columns};
            """;
        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("to", toSessionId);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("via", via);
        var taken = await ReadAllAsync(cmd, ct);
        return taken.OrderBy(m => m.CreatedAt).ToList();
    }

    public async Task<IReadOnlyList<SessionMessageRecord>> ListRecentAsync(string toSessionId, int limit, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT {Columns} FROM session_messages WHERE to_session_id = @to ORDER BY created_at DESC LIMIT @limit");
        cmd.Parameters.AddWithValue("to", toSessionId);
        cmd.Parameters.AddWithValue("limit", limit);
        return await ReadAllAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<SessionMessageRecord>> ListPageAsync(
        string owner, string toSessionId, MessagePageCursor? before, int limit, CancellationToken ct = default)
    {
        // A timestamp alone loses messages created together; the id breaks ties, and keyset
        // paging keeps newly arriving messages from shifting the next page underneath a reader.
        await using var cmd = _db.CreateCommand($"""
            SELECT {Columns} FROM session_messages
            WHERE owner = @owner AND to_session_id = @to
              {(before is null ? "" : "AND (created_at, id COLLATE \"C\") < (@created, @id COLLATE \"C\")")}
            ORDER BY created_at DESC, id COLLATE "C" DESC LIMIT @limit
            """);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("to", toSessionId);
        cmd.Parameters.AddWithValue("limit", limit);
        if (before is not null)
        {
            cmd.Parameters.AddWithValue("created", before.CreatedAt);
            cmd.Parameters.AddWithValue("id", before.Id);
        }
        return await ReadAllAsync(cmd, ct);
    }

    public async Task MarkDeliveredAsync(string id, string via, CancellationToken ct = default)
    {
        // The inbox poll and a push can race for the same row; whoever wins keeps the row, so
        // the sender is told about the delivery that actually happened rather than the last writer.
        await using var cmd = _db.CreateCommand(
            "UPDATE session_messages SET delivered_at = now(), delivered_via = @via WHERE id = @id AND delivered_at IS NULL");
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("via", via);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- helpers ----
    private const string Columns = "id, project_id, from_session_id, to_session_id, owner, body, created_at, delivered_at, priority, interrupt, delivered_via";

    private static async Task<List<SessionMessageRecord>> ReadAllAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<SessionMessageRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new SessionMessageRecord
            {
                Id = r.GetString(0),
                ProjectId = r.IsDBNull(1) ? null : r.GetString(1),
                FromSessionId = r.IsDBNull(2) ? null : r.GetString(2),
                ToSessionId = r.GetString(3),
                Owner = r.GetString(4),
                Body = r.GetString(5),
                CreatedAt = r.GetDateTime(6),
                DeliveredAt = r.IsDBNull(7) ? null : r.GetDateTime(7),
                Priority = r.GetBoolean(8),
                Interrupt = r.GetBoolean(9),
                DeliveredVia = r.IsDBNull(10) ? null : r.GetString(10)
            });
        }
        return list;
    }
}
