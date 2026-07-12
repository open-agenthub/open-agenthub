using AgentHub.Api.Models;
using Npgsql;

namespace AgentHub.Api.Persistence;

/// <summary>Persistent session record (registry/status). The actual Claude state lives in S3.</summary>
public sealed class SessionRecord
{
    public required string Id { get; init; }
    public required string Owner { get; init; }
    public string Title { get; set; } = "";
    public SessionMode Mode { get; set; }
    public string? RepoUrl { get; set; }
    public string? Schedule { get; set; }
    /// <summary>Claude Code session ID assigned by us (used for --resume).</summary>
    public required string ClaudeSessionId { get; init; }
    /// <summary>Pending | Running | Succeeded | Failed | Scheduled.</summary>
    public string Status { get; set; } = "Pending";
    public bool QuestionPending { get; set; }
    /// <summary>Custom container image (null = default agent image).</summary>
    public string? Image { get; set; }
    public bool RunAsRoot { get; set; }
    public string Cpu { get; set; } = "500m";
    public string Memory { get; set; } = "1Gi";
    /// <summary>MCP configuration (.mcp.json content); null/empty = no MCP servers.</summary>
    public string? McpConfigJson { get; set; }
    /// <summary>Repositories to check out, as a JSON array of {url,branch,providerId}. Null = none.
    /// (RepoUrl mirrors the first entry for display and backward compatibility.)</summary>
    public string? ReposJson { get; set; }
    /// <summary>Token the agent pod uses to authenticate against the internal callback.</summary>
    public required string CallbackToken { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public interface ISessionStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task UpsertAsync(SessionRecord r, CancellationToken ct = default);
    Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default);
    Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default);
    Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default);
    Task UpdateStatusAsync(string id, string status, CancellationToken ct = default);
    Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
}

public sealed class PostgresSessionStore : ISessionStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresSessionStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS sessions (
                id                TEXT PRIMARY KEY,
                owner             TEXT NOT NULL,
                title             TEXT NOT NULL DEFAULT '',
                mode              TEXT NOT NULL,
                repo_url          TEXT,
                schedule          TEXT,
                claude_session_id TEXT NOT NULL,
                status            TEXT NOT NULL DEFAULT 'Pending',
                question_pending  BOOLEAN NOT NULL DEFAULT FALSE,
                callback_token    TEXT NOT NULL,
                created_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at        TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS idx_sessions_owner ON sessions(owner);
            CREATE INDEX IF NOT EXISTS idx_sessions_token ON sessions(callback_token);
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS image TEXT;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS run_as_root BOOLEAN NOT NULL DEFAULT FALSE;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS cpu TEXT NOT NULL DEFAULT '500m';
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS memory TEXT NOT NULL DEFAULT '1Gi';
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS mcp_config TEXT;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS repos TEXT;
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(SessionRecord r, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO sessions (id, owner, title, mode, repo_url, schedule, claude_session_id,
                                  status, question_pending, callback_token, image, run_as_root, cpu, memory,
                                  mcp_config, repos, created_at, updated_at)
            VALUES (@id, @owner, @title, @mode, @repo, @sched, @csid, @status, @qp, @tok, @image, @root, @cpu, @memory,
                    @mcp, @repos, @created, now())
            ON CONFLICT (id) DO UPDATE SET
                title = EXCLUDED.title, mode = EXCLUDED.mode, repo_url = EXCLUDED.repo_url,
                schedule = EXCLUDED.schedule, status = EXCLUDED.status,
                question_pending = EXCLUDED.question_pending,
                image = EXCLUDED.image, run_as_root = EXCLUDED.run_as_root,
                cpu = EXCLUDED.cpu, memory = EXCLUDED.memory,
                mcp_config = EXCLUDED.mcp_config, repos = EXCLUDED.repos, updated_at = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        Bind(cmd, r);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default)
        => await QuerySingle("WHERE id = @p1 AND owner = @p2", ct, id, owner);

    public async Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default)
        => await QuerySingle("WHERE callback_token = @p1", ct, token);

    public async Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default)
    {
        var list = new List<SessionRecord>();
        await using var cmd = _db.CreateCommand($"{SelectBase} WHERE owner = @p1 ORDER BY created_at DESC");
        cmd.Parameters.AddWithValue("p1", owner);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task UpdateStatusAsync(string id, string status, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("UPDATE sessions SET status=@s, updated_at=now() WHERE id=@id");
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("UPDATE sessions SET question_pending=@p, updated_at=now() WHERE id=@id");
        cmd.Parameters.AddWithValue("p", pending);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM sessions WHERE id=@id");
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- helpers ----
    private const string SelectBase =
        "SELECT id, owner, title, mode, repo_url, schedule, claude_session_id, status, question_pending, callback_token, created_at, updated_at, image, run_as_root, cpu, memory, mcp_config, repos FROM sessions";

    private async Task<SessionRecord?> QuerySingle(string where, CancellationToken ct, params object[] ps)
    {
        await using var cmd = _db.CreateCommand($"{SelectBase} {where}");
        for (int i = 0; i < ps.Length; i++) cmd.Parameters.AddWithValue($"p{i + 1}", ps[i]);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    private static void Bind(NpgsqlCommand cmd, SessionRecord r)
    {
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("owner", r.Owner);
        cmd.Parameters.AddWithValue("title", r.Title);
        cmd.Parameters.AddWithValue("mode", r.Mode.ToString());
        cmd.Parameters.AddWithValue("repo", (object?)r.RepoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sched", (object?)r.Schedule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("csid", r.ClaudeSessionId);
        cmd.Parameters.AddWithValue("status", r.Status);
        cmd.Parameters.AddWithValue("qp", r.QuestionPending);
        cmd.Parameters.AddWithValue("tok", r.CallbackToken);
        cmd.Parameters.AddWithValue("image", (object?)r.Image ?? DBNull.Value);
        cmd.Parameters.AddWithValue("root", r.RunAsRoot);
        cmd.Parameters.AddWithValue("cpu", r.Cpu);
        cmd.Parameters.AddWithValue("memory", r.Memory);
        cmd.Parameters.AddWithValue("mcp", (object?)r.McpConfigJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("repos", (object?)r.ReposJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("created", r.CreatedAt);
    }

    private static SessionRecord Map(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0),
        Owner = r.GetString(1),
        Title = r.GetString(2),
        Mode = Enum.Parse<SessionMode>(r.GetString(3)),
        RepoUrl = r.IsDBNull(4) ? null : r.GetString(4),
        Schedule = r.IsDBNull(5) ? null : r.GetString(5),
        ClaudeSessionId = r.GetString(6),
        Status = r.GetString(7),
        QuestionPending = r.GetBoolean(8),
        CallbackToken = r.GetString(9),
        CreatedAt = r.GetDateTime(10),
        UpdatedAt = r.GetDateTime(11),
        Image = r.IsDBNull(12) ? null : r.GetString(12),
        RunAsRoot = r.GetBoolean(13),
        Cpu = r.GetString(14),
        Memory = r.GetString(15),
        McpConfigJson = r.IsDBNull(16) ? null : r.GetString(16),
        ReposJson = r.IsDBNull(17) ? null : r.GetString(17)
    };
}
