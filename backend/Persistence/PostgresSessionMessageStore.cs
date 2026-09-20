using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>
/// A message/task one project agent sent to another. <see cref="FromSessionId"/> is null
/// for messages sent from outside a session (remote API token). <see cref="DeliveredAt"/>
/// is set when the receiving agent takes the message from its inbox.
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
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
}

public interface ISessionMessageStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task AddAsync(SessionMessageRecord message, CancellationToken ct = default);
    /// <summary>Atomically marks up to <paramref name="limit"/> undelivered messages of the
    /// session as delivered and returns them (oldest first).</summary>
    Task<IReadOnlyList<SessionMessageRecord>> TakeUndeliveredAsync(string toSessionId, int limit, CancellationToken ct = default);
    /// <summary>Recent messages of the session (newest first), delivered or not — read-only view for the UI.</summary>
    Task<IReadOnlyList<SessionMessageRecord>> ListRecentAsync(string toSessionId, int limit, CancellationToken ct = default);
}

public sealed class PostgresSessionMessageStore : ISessionMessageStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresSessionMessageStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
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
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task AddAsync(SessionMessageRecord m, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO session_messages (id, project_id, from_session_id, to_session_id, owner, body, created_at, delivered_at)
            VALUES (@id, @project, @from, @to, @owner, @body, @created, @delivered);
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
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SessionMessageRecord>> TakeUndeliveredAsync(string toSessionId, int limit, CancellationToken ct = default)
    {
        const string sql = $"""
            UPDATE session_messages SET delivered_at = now()
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

    // ---- helpers ----
    private const string Columns = "id, project_id, from_session_id, to_session_id, owner, body, created_at, delivered_at";

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
                DeliveredAt = r.IsDBNull(7) ? null : r.GetDateTime(7)
            });
        }
        return list;
    }
}
