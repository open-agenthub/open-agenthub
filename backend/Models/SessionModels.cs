using System.Text.Json.Serialization;
using AgentHub.Api.Persistence;

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

public enum AgentKind { Claude, Codex }
public enum AgentAuthMode { Auto, Subscription, ApiKey }

public sealed record AgentPolicy
{
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedMcpTools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedCommands { get; init; } = Array.Empty<string>();
}

public static class AgentConfiguration
{
    public static void ValidateForCreate(AgentKind agent, AgentAuthMode authMode)
    {
        ValidateAgent(agent);
        ValidateAuthMode(authMode);
    }

    public static void ValidateForUpdate(AgentKind? agent, AgentAuthMode? authMode)
    {
        if (agent is { } selectedAgent) ValidateAgent(selectedAgent);
        if (authMode is { } selectedAuthMode) ValidateAuthMode(selectedAuthMode);
    }

    public static void ValidateForDuplicatedSession(AgentKind agent, AgentAuthMode authMode)
    {
        ValidateAgent(agent);
        if (agent == AgentKind.Claude && authMode == AgentAuthMode.Auto) return;
        ValidateAuthMode(authMode);
    }


    public static AgentPolicy ResolvePolicy(AgentPolicy policy, IReadOnlyList<string> legacyAllowedTools) =>
        IsEmpty(policy) && legacyAllowedTools.Count > 0
            ? policy with { AllowedTools = legacyAllowedTools.ToArray() }
            : policy;

    private static void ValidateAgent(AgentKind agent)
    {
        if (agent is not AgentKind.Claude and not AgentKind.Codex)
            throw new ArgumentException("Unsupported agent kind.");
    }

    private static void ValidateAuthMode(AgentAuthMode authMode)
    {
        if (authMode is not AgentAuthMode.Subscription and not AgentAuthMode.ApiKey)
            throw new ArgumentException("Authentication mode must be Subscription or ApiKey.");
    }

    private static bool IsEmpty(AgentPolicy policy) =>
        policy.AllowedTools.Count == 0 && policy.AllowedMcpTools.Count == 0 && policy.AllowedCommands.Count == 0;
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

    /// <summary>Optional personal project that owns the session grouping.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Cron expression, only for Scheduled (e.g. "0 6 * * 1-5").</summary>
    public string? Schedule { get; init; }

    /// <summary>MCP configuration as a JSON string (.mcp.json format), mounted into the container.</summary>
    public string? McpConfigJson { get; init; }

    public AgentKind Agent { get; init; } = AgentKind.Claude;
    public AgentAuthMode AuthMode { get; init; } = AgentAuthMode.Subscription;
    public AgentPolicy Policy { get; init; } = new();
    /// <summary>Deprecated compatibility input; used only when Policy is empty.</summary>
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
    public AgentKind? Agent { get; init; }
    public AgentAuthMode? AuthMode { get; init; }
    public AgentPolicy? Policy { get; init; }
    /// <summary>Replacement repo list; null = unchanged.</summary>
    public List<RepoRef>? Repos { get; init; }
    /// <summary>Replacement project assignment; null removes the assignment when supplied.</summary>
    private string? _projectId;
    [JsonIgnore]
    public bool ProjectIdSpecified { get; private set; }
    public string? ProjectId
    {
        get => _projectId;
        init { _projectId = value; ProjectIdSpecified = true; }
    }
}

public sealed record DuplicateSessionRequest(string Title, string? ProjectId, bool IncludeMcp,
    AgentKind? Agent = null, AgentAuthMode? AuthMode = null, AgentPolicy? Policy = null);

public static class SessionDuplication
{
    public static CreateSessionRequest CopyableRequest(SessionRecord source, DuplicateSessionRequest request) => new()
    {
        Title = request.Title,
        ProjectId = request.ProjectId,
        Mode = source.Mode,
        Repos = Deserialize<List<RepoRef>>(source.ReposJson),
        RepoUrl = source.RepoUrl,
        Prompt = source.Prompt,
        Schedule = source.Schedule,
        McpConfigJson = request.IncludeMcp ? source.McpConfigJson : null,
        Agent = request.Agent ?? source.Agent,
        AuthMode = request.AuthMode ?? source.AuthMode,
        Policy = request.Policy ?? Deserialize<AgentPolicy>(source.AgentPolicyJson),
        // An explicit structured policy, including an empty default-deny policy,
        // supersedes legacy AllowedTools instead of rehydrating it later.
        AllowedTools = request.Policy is null ? Deserialize<List<string>>(source.AllowedToolsJson) : new List<string>(),
        Image = source.Image,
        RunAsRoot = source.RunAsRoot,
        Cpu = source.Cpu,
        Memory = source.Memory
    };

    private static T Deserialize<T>(string? json) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json)) return new T();
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new T(); }
        catch (System.Text.Json.JsonException) { return new T(); }
    }
}

/// <summary>View of a running or scheduled session.</summary>
public record SessionInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Owner { get; init; }
    public string? ProjectId { get; init; }
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
    public string? Prompt { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
    public AgentKind Agent { get; init; } = AgentKind.Claude;
    public AgentAuthMode AuthMode { get; init; } = AgentAuthMode.Auto;
    public AgentPolicy Policy { get; init; } = new();
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
    public string? OpenAiApiKey { get; init; }
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
    public bool OpenAiApiKey { get; init; }
    public bool GitUserName { get; init; }
    public bool GitUserEmail { get; init; }
    public bool ClaudeSubscription { get; init; }
    public bool CodexSubscription { get; init; }
}
