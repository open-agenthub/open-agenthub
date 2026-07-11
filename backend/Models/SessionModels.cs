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

/// <summary>Request to start a new session.</summary>
public record CreateSessionRequest
{
    public string Title { get; init; } = "Untitled";
    public SessionMode Mode { get; init; } = SessionMode.Interactive;

    /// <summary>Optional repo cloned at startup (SSH or HTTPS URL).</summary>
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

/// <summary>View of a running or scheduled session.</summary>
public record SessionInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Owner { get; init; }
    public required SessionMode Mode { get; init; }
    public string? RepoUrl { get; init; }
    public required string Phase { get; init; }       // Pending | Running | Succeeded | Failed | Scheduled
    public string? PodIp { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Schedule { get; init; }
    public bool QuestionPending { get; init; }
    /// <summary>A finished session with saved state can be resumed.</summary>
    public bool CanResume { get; init; }
    /// <summary>Custom image of the session (null = default agent image).</summary>
    public string? Image { get; init; }
    public bool RunAsRoot { get; init; }
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
}
