namespace AgentHub.Api.Models;

/// <summary>Operating mode of an agent session.</summary>
public enum SessionMode
{
    /// <summary>Interactive: Claude runs in the TUI; questions are answered in the web UI.</summary>
    Interactive,
    /// <summary>Autonomous: Claude works through a prompt without follow-up questions (limited by allowlist).</summary>
    Autonomous,
    /// <summary>Scheduled: creates a CronJob that starts the task on a schedule.</summary>
    Scheduled
}

/// <summary>A repository to check out into the session workspace.</summary>
public record RepoRef
{
    /// <summary>Clone URL (SSH or HTTPS).</summary>
    public required string Url { get; init; }
    public string? Branch { get; init; }
    /// <summary>Id of the connected Git provider whose OAuth token authenticates the
    /// clone/push (null = anonymous, SSH key, or manual PAT).</summary>
    public string? ProviderId { get; init; }
}

/// <summary>Request to start a new session.</summary>
public record CreateSessionRequest
{
    public string Title { get; init; } = "Untitled";
    public SessionMode Mode { get; init; } = SessionMode.Interactive;

    /// <summary>Repositories cloned at startup (each into /workspace/&lt;name&gt;).</summary>
    public List<RepoRef> Repos { get; init; } = new();

    /// <summary>Legacy single-repo fields; folded into Repos when Repos is empty.</summary>
    public string? RepoUrl { get; init; }
    public string? RepoBranch { get; init; }

    /// <summary>Initial prompt – required for Autonomous/Scheduled.</summary>
    public string? Prompt { get; init; }

    /// <summary>Cron expression, only for Scheduled (e.g. "0 6 * * 1-5").</summary>
    public string? Schedule { get; init; }

    /// <summary>MCP configuration as a JSON string (.mcp.json format), mounted into the container.</summary>
    public string? McpConfigJson { get; init; }

    /// <summary>Allowlist of permitted tools for autonomous mode.</summary>
    public List<string> AllowedTools { get; init; } = new();

    /// <summary>Custom container image (glibc-based, bash+git+curl recommended). Empty = default agent image.</summary>
    public string? Image { get; init; }

    /// <summary>Run as root inside the container so tools can be installed (apt, npm -g, …).
    /// The pod stays unprivileged (no privileged mode, no hostPath, NetworkPolicies apply).</summary>
    public bool RunAsRoot { get; init; }

    public string Cpu { get; init; } = "500m";
    public string Memory { get; init; } = "1Gi";
}

/// <summary>
/// Partial update of an existing session. Null = unchanged. Everything except
/// the title only takes effect the next time the session is (re)started.
/// </summary>
public record UpdateSessionRequest
{
    public string? Title { get; init; }
    /// <summary>Custom container image; empty string resets to the default agent image.</summary>
    public string? Image { get; init; }
    public bool? RunAsRoot { get; init; }
    public string? Cpu { get; init; }
    public string? Memory { get; init; }
    /// <summary>MCP config (.mcp.json); null = unchanged, empty string = remove all MCP servers.</summary>
    public string? McpConfigJson { get; init; }
    /// <summary>Replacement repo list; null = unchanged.</summary>
    public List<RepoRef>? Repos { get; init; }
}

/// <summary>View of a running or scheduled session.</summary>
public record SessionInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Owner { get; init; }
    public required SessionMode Mode { get; init; }
    /// <summary>First repo URL (backward-compatible display field).</summary>
    public string? RepoUrl { get; init; }
    public List<RepoRef> Repos { get; init; } = new();
    /// <summary>Whether the session has an MCP configuration.</summary>
    public bool HasMcp { get; init; }
    /// <summary>MCP config JSON (returned so the edit dialog can prefill it).</summary>
    public string? McpConfigJson { get; init; }
    public required string Phase { get; init; }       // Pending | Running | Paused | Succeeded | Failed | Scheduled
    public string? PodIp { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Schedule { get; init; }
    public bool QuestionPending { get; init; }
    /// <summary>A finished session with saved state can be resumed.</summary>
    public bool CanResume { get; init; }
    /// <summary>Custom image of the session (null = default agent image).</summary>
    public string? Image { get; init; }
    public bool RunAsRoot { get; init; }
    public string Cpu { get; init; } = "500m";
    public string Memory { get; init; } = "1Gi";
}

/// <summary>
/// Per-user credentials. These are written to a per-user Kubernetes secret
/// rather than being stored in plaintext in the database.
/// </summary>
public record UserCredentials
{
    public string? SshPrivateKey { get; init; }
    public string? GitlabToken { get; init; }
    public string? AnthropicApiKey { get; init; }
    /// <summary>known_hosts entry of the GitLab server (protects against MITM on the first clone).</summary>
    public string? GitKnownHosts { get; init; }
    public string? GitUserName { get; init; }
    public string? GitUserEmail { get; init; }
    /// <summary>Field names (camelCase, e.g. "gitlabToken") whose stored value should be removed.
    /// Empty fields are otherwise left unchanged (merge semantics).</summary>
    public List<string> Clear { get; init; } = new();
}

/// <summary>Which credential fields currently have a stored value (values are never returned).</summary>
public record CredentialStatus
{
    public bool SshPrivateKey { get; init; }
    public bool GitlabToken { get; init; }
    public bool AnthropicApiKey { get; init; }
    public bool GitKnownHosts { get; init; }
    public bool GitUserName { get; init; }
    public bool GitUserEmail { get; init; }
}
