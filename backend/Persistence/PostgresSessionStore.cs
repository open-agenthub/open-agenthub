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
    /// <summary>UI rendering mode: "terminal" or "chat".</summary>
    public string UiMode { get; set; } = SessionUiMode.Terminal;
    public string? RepoUrl { get; set; }
    public string? Schedule { get; set; }
    public string? ProjectId { get; set; }
    /// <summary>Optional parent session id for orchestration; null = root. No FK cascade.</summary>
    public string? ParentSessionId { get; set; }
    public string? Prompt { get; set; }
    public AgentKind Agent { get; set; } = AgentKind.Claude;
    public AgentAuthMode AuthMode { get; set; } = AgentAuthMode.Auto;
    /// <summary>Which existing API key OpenClaw should use; set only for OpenClaw + ApiKey.</summary>
    public OpenClawApiKeySource? OpenClawApiKeySource { get; set; }
    public string? AgentPolicyJson { get; set; }
    public string? AllowedToolsJson { get; set; }
    /// <summary>Agent session ID assigned by us (used for --resume).</summary>
    public string AgentSessionId { get; init; } = "";
    /// <summary>Legacy compatibility input; not used by application mappings.</summary>
    public string? ClaudeSessionId { get; init; }
    /// <summary>Pending | Running | Succeeded | Failed | Scheduled.</summary>
    public string Status { get; set; } = "Pending";
    public bool QuestionPending { get; set; }
    /// <summary>Custom container image (null = default agent image).</summary>
    public string? Image { get; set; }
    public bool RunAsRoot { get; set; }
    /// <summary>Tool-permission requests are approved without asking. Read per request,
    /// so toggling it applies to an already running session.</summary>
    public bool AutoApprove { get; set; }
    public string Cpu { get; set; } = "500m";
    public string Memory { get; set; } = "1Gi";
    /// <summary>MCP configuration (.mcp.json content); null/empty = no MCP servers.</summary>
    public string? McpConfigJson { get; set; }
    /// <summary>JSON array of catalog MCP server ids merged into the effective
    /// config at spawn time. Null = none.</summary>
    public string? McpServerIdsJson { get; set; }
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
    Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
    Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default);
    Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default);
    Task UpdateStatusAsync(string id, string status, CancellationToken ct = default);
    Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default);
    /// <summary>Stores the terminal scrollback so transcripts work without S3.</summary>
    Task SetScrollbackAsync(string id, string text, CancellationToken ct = default);
    Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default);
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
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS scrollback TEXT;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS project_id TEXT;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS prompt TEXT;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS allowed_tools TEXT;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS agent TEXT NOT NULL DEFAULT 'Claude';
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS auth_mode TEXT NOT NULL DEFAULT 'Auto';
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS openclaw_api_key_source TEXT;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS agent_policy JSONB;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS agent_session_id TEXT;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS ui_mode TEXT NOT NULL DEFAULT 'terminal';
            UPDATE sessions SET agent_session_id = claude_session_id WHERE agent_session_id IS NULL;
            ALTER TABLE sessions ALTER COLUMN agent_session_id SET NOT NULL;
            ALTER TABLE sessions ALTER COLUMN claude_session_id DROP NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_sessions_project ON sessions(owner, project_id);
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS parent_session_id TEXT;
            CREATE INDEX IF NOT EXISTS idx_sessions_parent ON sessions(owner, parent_session_id);
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS mcp_server_ids TEXT;
            ALTER TABLE sessions ADD COLUMN IF NOT EXISTS auto_approve BOOLEAN NOT NULL DEFAULT FALSE;
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(SessionRecord r, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO sessions (id, owner, title, mode, ui_mode, repo_url, schedule, agent_session_id, agent, auth_mode,
                                  openclaw_api_key_source, agent_policy,
                                  status, question_pending, callback_token, image, run_as_root, cpu, memory,
                                  mcp_config, mcp_server_ids, repos, project_id, parent_session_id, prompt, allowed_tools, auto_approve, created_at, updated_at)
            VALUES (@id, @owner, @title, @mode, @uiMode, @repo, @sched, @agentSessionId, @agent, @authMode,
                    @openClawApiKeySource, @policy,
                    @status, @qp, @tok, @image, @root, @cpu, @memory,
                    @mcp, @mcpServerIds, @repos, @project, @parent, @prompt, @allowedTools, @autoApprove, @created, now())
            ON CONFLICT (id) DO UPDATE SET
                title = EXCLUDED.title, mode = EXCLUDED.mode, ui_mode = EXCLUDED.ui_mode, repo_url = EXCLUDED.repo_url,
                schedule = EXCLUDED.schedule, status = EXCLUDED.status,
                question_pending = EXCLUDED.question_pending,
                agent_session_id = EXCLUDED.agent_session_id,
                agent = EXCLUDED.agent, auth_mode = EXCLUDED.auth_mode,
                openclaw_api_key_source = EXCLUDED.openclaw_api_key_source,
                agent_policy = EXCLUDED.agent_policy,
                image = EXCLUDED.image, run_as_root = EXCLUDED.run_as_root,
                cpu = EXCLUDED.cpu, memory = EXCLUDED.memory,
                mcp_config = EXCLUDED.mcp_config, mcp_server_ids = EXCLUDED.mcp_server_ids,
                repos = EXCLUDED.repos,
                project_id = EXCLUDED.project_id, parent_session_id = EXCLUDED.parent_session_id,
                prompt = EXCLUDED.prompt,
                allowed_tools = EXCLUDED.allowed_tools, auto_approve = EXCLUDED.auto_approve, updated_at = now();
            """;
        await using var cmd = _db.CreateCommand(sql);
        Bind(cmd, r);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default)
        => await QuerySingle("WHERE id = @p1 AND owner = @p2", ct, id, owner);

    public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default)
        => QuerySingle("WHERE id = @p1", ct, id);

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

    public async Task SetScrollbackAsync(string id, string text, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("UPDATE sessions SET scrollback=@s, updated_at=now() WHERE id=@id");
        cmd.Parameters.AddWithValue("s", text);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT scrollback FROM sessions WHERE id=@id");
        cmd.Parameters.AddWithValue("id", id);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is string s ? s : null;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("DELETE FROM sessions WHERE id=@id");
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- helpers ----
    private const string SelectBase =
        "SELECT id, owner, title, mode, repo_url, schedule, agent_session_id, agent, auth_mode, openclaw_api_key_source, agent_policy, status, question_pending, callback_token, created_at, updated_at, image, run_as_root, cpu, memory, mcp_config, repos, project_id, parent_session_id, prompt, allowed_tools, ui_mode, mcp_server_ids, auto_approve FROM sessions";

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
        cmd.Parameters.AddWithValue("uiMode", r.UiMode);
        cmd.Parameters.AddWithValue("repo", (object?)r.RepoUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("sched", (object?)r.Schedule ?? DBNull.Value);
        cmd.Parameters.AddWithValue("agentSessionId", r.AgentSessionId);
        cmd.Parameters.AddWithValue("agent", r.Agent.ToString());
        cmd.Parameters.AddWithValue("authMode", r.AuthMode.ToString());
        cmd.Parameters.AddWithValue("openClawApiKeySource", (object?)r.OpenClawApiKeySource?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("policy", NpgsqlTypes.NpgsqlDbType.Jsonb, (object?)r.AgentPolicyJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("status", r.Status);
        cmd.Parameters.AddWithValue("qp", r.QuestionPending);
        cmd.Parameters.AddWithValue("tok", r.CallbackToken);
        cmd.Parameters.AddWithValue("image", (object?)r.Image ?? DBNull.Value);
        cmd.Parameters.AddWithValue("root", r.RunAsRoot);
        cmd.Parameters.AddWithValue("autoApprove", r.AutoApprove);
        cmd.Parameters.AddWithValue("cpu", r.Cpu);
        cmd.Parameters.AddWithValue("memory", r.Memory);
        cmd.Parameters.AddWithValue("mcp", (object?)r.McpConfigJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("mcpServerIds", (object?)r.McpServerIdsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("repos", (object?)r.ReposJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("project", (object?)r.ProjectId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("parent", (object?)r.ParentSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("prompt", (object?)r.Prompt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("allowedTools", (object?)r.AllowedToolsJson ?? DBNull.Value);
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
        AgentSessionId = r.GetString(6),
        Agent = Enum.Parse<AgentKind>(r.GetString(7)),
        AuthMode = Enum.Parse<AgentAuthMode>(r.GetString(8)),
        OpenClawApiKeySource = r.IsDBNull(9) ? null : Enum.Parse<OpenClawApiKeySource>(r.GetString(9)),
        AgentPolicyJson = r.IsDBNull(10) ? null : r.GetString(10),
        Status = r.GetString(11),
        QuestionPending = r.GetBoolean(12),
        CallbackToken = r.GetString(13),
        CreatedAt = r.GetDateTime(14),
        UpdatedAt = r.GetDateTime(15),
        Image = r.IsDBNull(16) ? null : r.GetString(16),
        RunAsRoot = r.GetBoolean(17),
        Cpu = r.GetString(18),
        Memory = r.GetString(19),
        McpConfigJson = r.IsDBNull(20) ? null : r.GetString(20),
        ReposJson = r.IsDBNull(21) ? null : r.GetString(21),
        ProjectId = r.IsDBNull(22) ? null : r.GetString(22),
        ParentSessionId = r.IsDBNull(23) ? null : r.GetString(23),
        Prompt = r.IsDBNull(24) ? null : r.GetString(24),
        AllowedToolsJson = r.IsDBNull(25) ? null : r.GetString(25),
        UiMode = r.GetString(26),
        McpServerIdsJson = r.IsDBNull(27) ? null : r.GetString(27),
        AutoApprove = r.GetBoolean(28)
    };
}
