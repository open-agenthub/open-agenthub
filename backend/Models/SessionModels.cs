using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHub.Api.Browser;
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

public enum AgentKind { Claude, Codex, Cursor, OpenClaw }
public enum AgentAuthMode { Auto, Subscription, ApiKey }
public enum OpenClawApiKeySource { Anthropic, OpenAI, Cursor }

public sealed record AgentPolicy
{
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedMcpTools { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AllowedCommands { get; init; } = Array.Empty<string>();
}

public static class AgentConfiguration
{
    public static void ValidateForCreate(AgentKind agent, AgentAuthMode authMode,
        OpenClawApiKeySource? openClawApiKeySource = null)
    {
        ValidateAgent(agent);
        ValidateAuthMode(authMode);
        ValidateOpenClawApiKeySource(agent, authMode, openClawApiKeySource);
    }

    public static void ValidateForUpdate(AgentKind? agent, AgentAuthMode? authMode,
        OpenClawApiKeySource? openClawApiKeySource = null)
    {
        if (agent is { } selectedAgent) ValidateAgent(selectedAgent);
        if (authMode is { } selectedAuthMode) ValidateAuthMode(selectedAuthMode);
        if (agent is { } a && authMode is { } m)
            ValidateOpenClawApiKeySource(a, m, openClawApiKeySource);
        else if (openClawApiKeySource is not null)
            throw new ArgumentException("OpenClaw API key source is only valid for OpenClaw with ApiKey authentication.");
    }

    public static void ValidateForUpdate(AgentKind currentAgent, AgentAuthMode currentAuthMode,
        AgentKind? requestedAgent, AgentAuthMode? requestedAuthMode,
        OpenClawApiKeySource? currentOpenClawApiKeySource = null,
        OpenClawApiKeySource? requestedOpenClawApiKeySource = null)
    {
        // A migrated Claude+Auto record may remain untouched, but Auto is never a
        // valid result once the public PATCH supplies either agent/auth/source field.
        if (requestedAgent is null && requestedAuthMode is null && requestedOpenClawApiKeySource is null) return;

        var agent = requestedAgent ?? currentAgent;
        var authMode = requestedAuthMode ?? currentAuthMode;
        var openClawApiKeySource = agent == AgentKind.OpenClaw && authMode == AgentAuthMode.ApiKey
            ? requestedOpenClawApiKeySource ?? currentOpenClawApiKeySource
            : requestedOpenClawApiKeySource;

        // Source-only updates keep the current agent/auth pair (including migrated Auto).
        if (requestedAgent is not null || requestedAuthMode is not null)
        {
            ValidateAgent(agent);
            ValidateAuthMode(authMode);
        }

        ValidateOpenClawApiKeySource(agent, authMode, openClawApiKeySource);
    }

    public static void ValidateForDuplicatedSession(AgentKind agent, AgentAuthMode authMode,
        OpenClawApiKeySource? openClawApiKeySource = null)
    {
        ValidateAgent(agent);
        if (agent == AgentKind.Claude && authMode == AgentAuthMode.Auto) return;
        ValidateAuthMode(authMode);
        ValidateOpenClawApiKeySource(agent, authMode, openClawApiKeySource);
    }


    public static AgentPolicy ResolvePolicy(AgentPolicy? policy, IReadOnlyList<string> legacyAllowedTools) =>
        policy
        ?? (legacyAllowedTools.Count > 0
            ? new AgentPolicy { AllowedTools = legacyAllowedTools.ToArray() }
            : new AgentPolicy());

    private static void ValidateAgent(AgentKind agent)
    {
        if (agent is not AgentKind.Claude and not AgentKind.Codex and not AgentKind.Cursor
            and not AgentKind.OpenClaw)
            throw new ArgumentException("Unsupported agent kind.");
    }

    private static void ValidateAuthMode(AgentAuthMode authMode)
    {
        if (authMode is not AgentAuthMode.Subscription and not AgentAuthMode.ApiKey)
            throw new ArgumentException("Authentication mode must be Subscription or ApiKey.");
    }

    private static void ValidateOpenClawApiKeySource(AgentKind agent, AgentAuthMode authMode,
        OpenClawApiKeySource? openClawApiKeySource)
    {
        var openClawApiKey = agent == AgentKind.OpenClaw && authMode == AgentAuthMode.ApiKey;
        if (openClawApiKey)
        {
            if (openClawApiKeySource is null || !Enum.IsDefined(openClawApiKeySource.Value))
                throw new ArgumentException("OpenClaw API key source is required for OpenClaw ApiKey authentication.");
            return;
        }

        if (openClawApiKeySource is not null)
            throw new ArgumentException("OpenClaw API key source is only valid for OpenClaw with ApiKey authentication.");
    }
}

/// <summary>How the frontend renders a session: terminal (PTY) or chat (stream-json).</summary>
public static class SessionUiMode
{
    public const string Terminal = "terminal";
    public const string Chat = "chat";

    /// <summary>Normalizes the requested UI mode (case-insensitive, empty = terminal) and
    /// rejects unsupported values or agent/mode combinations.</summary>
    public static string NormalizeForCreate(string? uiMode, AgentKind agent, SessionMode mode)
    {
        var normalized = string.IsNullOrWhiteSpace(uiMode) ? Terminal : uiMode.Trim().ToLowerInvariant();
        if (normalized is not (Terminal or Chat))
            throw new ArgumentException("UI mode must be 'terminal' or 'chat'.");
        if (normalized == Chat && (agent != AgentKind.Claude || mode != SessionMode.Interactive))
            throw new ArgumentException("Chat UI mode is only supported for interactive Claude sessions.");
        return normalized;
    }
}

/// <summary>Normalizes the optional agent description (trimmed, empty → null, length-capped).</summary>
public static class SessionDescription
{
    public const int MaxLength = 500;

    public static string? Normalize(string? description)
    {
        var trimmed = description?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > MaxLength)
            throw new ArgumentException($"The description is limited to {MaxLength} characters.");
        return trimmed;
    }
}

/// <summary>
/// Normalizes the optional extra system prompt (trimmed, empty → null, length-capped).
///
/// The cap is there because the value travels to the agent as a pod environment variable: a
/// megabyte of instructions would not be rejected by validation but by the Kubernetes API when it
/// refused the oversized pod spec, which surfaces as an opaque session that never starts.
/// </summary>
public static class SessionSystemPrompt
{
    public const int MaxLength = 20_000;

    public static string? Normalize(string? systemPrompt)
    {
        var trimmed = systemPrompt?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > MaxLength)
            throw new ArgumentException($"The system prompt is limited to {MaxLength} characters.");
        return trimmed;
    }
}

/// <summary>
/// Validates the repository list a caller asked for.
///
/// None of this was checked before, which was tolerable while the only producers were a picker in
/// the browser and a chat command. An API or MCP caller composes the list itself, so the limits
/// have to live in the backend — the one cap that existed was <c>.max(32)</c> in the stdio server's
/// schema, which the REST API does not go through.
/// </summary>
public static class SessionRepos
{
    /// <summary>
    /// Each repository is one clone in a single init container, run in sequence. The cap is about
    /// the failure beyond it: a list of several hundred either runs for hours or is rejected by the
    /// Kubernetes API for pod-spec size, and both surface as a session that never starts for no
    /// visible reason.
    /// </summary>
    public const int MaxCount = 16;

    public const int MaxUrlLength = 2048;
    public const int MaxBranchLength = 256;

    /// <summary>
    /// Transports git can be asked for over the wire. <c>file://</c> and <c>ext::</c> are the
    /// notable omissions: <c>ext::</c> runs an arbitrary command as a transport helper, and
    /// <c>file://</c> would read paths inside the pod rather than a repository. Neither is
    /// something a caller naming a repository needs.
    /// </summary>
    private static readonly string[] AllowedSchemes = ["https://", "http://", "ssh://", "git://"];

    public static void Validate(IReadOnlyList<RepoRef> repos)
    {
        if (repos.Count > MaxCount)
            throw new ArgumentException($"A session is limited to {MaxCount} repositories.");

        foreach (var repo in repos)
        {
            var url = repo.Url?.Trim() ?? "";
            if (url.Length > MaxUrlLength)
                throw new ArgumentException($"A repository URL is limited to {MaxUrlLength} characters.");
            if ((repo.Branch?.Length ?? 0) > MaxBranchLength)
                throw new ArgumentException($"A branch name is limited to {MaxBranchLength} characters.");
            // A leading dash would reach `git clone` as an option rather than a URL. The script
            // quotes it, so this is not shell injection — but `--upload-pack=` is git's own.
            if (url.StartsWith('-'))
                throw new ArgumentException("A repository URL must not start with '-'.");
            if (!IsAcceptable(url))
                throw new ArgumentException(
                    $"Unsupported repository URL '{Truncate(url)}'. Use https, http, ssh or git, "
                    + "or scp-style user@host:path.");
        }
    }

    private static bool IsAcceptable(string url)
    {
        if (AllowedSchemes.Any(s => url.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return true;
        // scp-style: user@host:path, which is how an SSH remote is usually written and what the
        // credentials dialog's own help text suggests. Rejecting a scheme-less string outright
        // would turn a working configuration into an error.
        var at = url.IndexOf('@');
        var colon = url.IndexOf(':');
        return at > 0 && colon > at + 1 && !url.Contains("://", StringComparison.Ordinal);
    }

    private static string Truncate(string url) => url.Length <= 80 ? url : url[..80] + "…";
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

    /// <summary>What this agent is for — shown in the UI and to other agents of the project.</summary>
    public string? Description { get; init; }

    public SessionMode Mode { get; init; } = SessionMode.Interactive;

    /// <summary>UI rendering mode: "terminal" (default) or "chat" (interactive Claude only).</summary>
    public string UiMode { get; init; } = SessionUiMode.Terminal;

    /// <summary>Repositories cloned at startup (each into /workspace/&lt;name&gt;).</summary>
    public List<RepoRef> Repos { get; init; } = new();

    /// <summary>Legacy single-repo fields; folded into Repos when Repos is empty.</summary>
    public string? RepoUrl { get; init; }
    public string? RepoBranch { get; init; }

    /// <summary>Initial prompt – required for Autonomous/Scheduled. An Interactive session is
    /// started on it too and then stays live, so a caller can create a session that is already
    /// working and hand its URL to a person later.</summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// Extra instructions appended to the agent's own system prompt — the caller's standing rules
    /// for the session, as opposed to the task in <see cref="Prompt"/>.
    ///
    /// Appended, never substituted: every one of the four agent CLIs also offers a way to *replace*
    /// its system prompt, and each of those takes the CLI's own tool and environment instructions
    /// with it, so one line of persona would disable the agent. Each runtime reaches the appending
    /// path differently — see the <c>session-prompt</c> module under the runtime in question.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Optional personal project that owns the session grouping.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Optional parent session for orchestration (child sessions).</summary>
    public string? ParentSessionId { get; init; }

    /// <summary>Cron expression, only for Scheduled (e.g. "0 6 * * 1-5").</summary>
    public string? Schedule { get; init; }

    /// <summary>MCP configuration as a JSON string (.mcp.json format), mounted into the container.</summary>
    public string? McpConfigJson { get; init; }

    /// <summary>Ids of catalog MCP servers to include (own, org, or EE-shared).
    /// Merged with McpConfigJson into the effective .mcp.json; inline entries win.</summary>
    public List<string> McpServerIds { get; init; } = new();

    /// <summary>
    /// Session-scoped OpenAPI/GraphQL MCP sources registered on the gateway for this session only.
    /// Optional <see cref="EphemeralApiSource.SaveToLibrary"/> also creates a personal catalog entry.
    /// </summary>
    public List<EphemeralApiSource> EphemeralApiSources { get; init; } = new();

    public AgentKind Agent { get; init; } = AgentKind.Claude;
    public AgentAuthMode AuthMode { get; init; } = AgentAuthMode.Subscription;
    /// <summary>Which existing API key OpenClaw should use; required only for OpenClaw + ApiKey.</summary>
    public OpenClawApiKeySource? OpenClawApiKeySource { get; init; }
    /// <summary>
    /// Which stored provider login (account) a Subscription session mounts. Null means the
    /// default account, resolved at every start — so a default changed later applies to the next
    /// resume — while an explicit id pins the session to that login. Checked against the user's
    /// accounts when the session is created, so an unknown id fails the request, not the pod.
    /// </summary>
    public string? CredentialId { get; init; }
    /// <summary>Structured policy. When supplied, including as an empty object, it supersedes AllowedTools.</summary>
    public AgentPolicy? Policy { get; init; }
    /// <summary>Deprecated compatibility input; used only when Policy is omitted.</summary>
    public List<string> AllowedTools { get; init; } = new();

    /// <summary>Custom container image (glibc-based, bash+git+curl recommended). Empty = default agent image.</summary>
    public string? Image { get; init; }

    /// <summary>Run as root inside the container so tools can be installed (apt, npm -g, …).
    /// The pod stays unprivileged (no privileged mode, no hostPath, NetworkPolicies apply).</summary>
    public bool RunAsRoot { get; init; }

    /// <summary>
    /// Approve every tool-permission request of this session automatically, without asking in
    /// the web app or the messengers.
    ///
    /// Null means "decide from the mode", which is what <see cref="AutoApproveFor"/> does:
    /// on by default for Autonomous and Scheduled sessions, off for Interactive ones. Nobody is
    /// watching an unattended session, so a permission prompt there has no one to answer it —
    /// the agent stalls on its first tool call and reports back having done nothing.
    /// Set it explicitly to false to keep an autonomous session gated anyway.
    /// </summary>
    public bool? AutoApprove { get; init; }

    /// <summary>The effective auto-approve setting: an explicit choice, else the mode's default.</summary>
    public static bool AutoApproveFor(bool? requested, SessionMode mode) =>
        requested ?? mode is SessionMode.Autonomous or SessionMode.Scheduled;

    public string Cpu { get; init; } = "500m";
    public string Memory { get; init; } = "1Gi";
}

/// <summary>Session-scoped OpenAPI/GraphQL source registered on the in-process MCP gateway.</summary>
public sealed record EphemeralApiSource
{
    public string Name { get; init; } = "";
    public string SpecUrl { get; init; } = "";
    public string? SpecType { get; init; }
    public string? BaseUrl { get; init; }
    public JsonElement? Auth { get; init; }
    public string? Secret { get; init; }
    /// <summary>When true, also create a personal <c>kind=api</c> catalog entry.</summary>
    public bool SaveToLibrary { get; init; }
}

/// <summary>
/// Partial update of an existing session. Null = unchanged. Everything except
/// the title only takes effect the next time the session is (re)started.
/// </summary>
public record UpdateSessionRequest
{
    public string? Title { get; init; }
    /// <summary>Agent description; null = unchanged, empty string clears it. Applies immediately.</summary>
    public string? Description { get; init; }
    /// <summary>
    /// Extra system-prompt instructions (see <see cref="CreateSessionRequest.SystemPrompt"/>);
    /// null = unchanged, empty string clears it. Same normalization and cap as on create.
    ///
    /// Takes effect on the next start or resume, not on the live pod: the value reaches the agent
    /// as a pod environment variable, and the resume path rebuilds the create request from the
    /// stored record, so the record is the only place that has to change.
    /// </summary>
    public string? SystemPrompt { get; init; }
    /// <summary>Custom container image; empty string resets to the default agent image.</summary>
    public string? Image { get; init; }
    public bool? RunAsRoot { get; init; }
    /// <summary>Auto-approve tool permissions. Unlike the other fields this takes effect
    /// immediately — the backend evaluates it per permission request, so it can be flipped
    /// on a running session without a restart.</summary>
    public bool? AutoApprove { get; init; }
    public string? Cpu { get; init; }
    public string? Memory { get; init; }
    /// <summary>Inline MCP config (.mcp.json); null = unchanged, empty string clears inline
    /// config only (catalog <see cref="McpServerIds"/> are unchanged unless also sent).</summary>
    public string? McpConfigJson { get; init; }
    /// <summary>Catalog MCP servers; null = unchanged, empty list = none.</summary>
    public List<string>? McpServerIds { get; init; }
    /// <summary>
    /// Session-scoped OpenAPI/GraphQL MCP sources; null = unchanged, empty list clears,
    /// non-empty replaces all ephemerals for the session (then MCP secret is re-assembled).
    /// </summary>
    public List<EphemeralApiSource>? EphemeralApiSources { get; init; }
    public AgentKind? Agent { get; init; }
    public AgentAuthMode? AuthMode { get; init; }
    /// <summary>Which existing API key OpenClaw should use; only for OpenClaw + ApiKey.</summary>
    public OpenClawApiKeySource? OpenClawApiKeySource { get; init; }
    /// <summary>Provider account for the next start; null = unchanged, empty string = back to the
    /// default account. A running session is switched live through the credential endpoint instead.</summary>
    public string? CredentialId { get; init; }
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

/// <param name="SystemPrompt">Replaces the copied system prompt; null copies the source's, an
/// empty string yields a copy without one (create-side normalization turns it into null).</param>
public sealed record DuplicateSessionRequest(string Title, string? ProjectId, bool IncludeMcp,
    AgentKind? Agent = null, AgentAuthMode? AuthMode = null, AgentPolicy? Policy = null,
    OpenClawApiKeySource? OpenClawApiKeySource = null,
    List<string>? McpServerIds = null,
    string? SystemPrompt = null, string? CredentialId = null);

public static class SessionDuplication
{
    public static CreateSessionRequest CopyableRequest(SessionRecord source, DuplicateSessionRequest request)
    {
        var agent = request.Agent ?? source.Agent;
        var authMode = request.AuthMode ?? source.AuthMode;
        return new()
        {
            // An account belongs to one provider, so the source's choice only carries over while
            // the copy keeps the agent; a copy switched to another agent falls back to its default.
            // It is dropped for an API-key copy too: creation rejects any account on one, so
            // carrying it over would turn "copy as API key" into a 400 about a field never shown.
            CredentialId = request.CredentialId
                ?? (agent == source.Agent && authMode != AgentAuthMode.ApiKey ? source.CredentialId : null),
            Title = request.Title,
            Description = source.Description,
            ProjectId = request.ProjectId,
            Mode = source.Mode,
            UiMode = source.UiMode,
            Repos = Deserialize<List<RepoRef>>(source.ReposJson),
            RepoUrl = source.RepoUrl,
            Prompt = source.Prompt,
            // The standing rules belong to the configuration being copied, unlike the
            // conversation they shaped; the duplicate dialog shows them so they can be edited.
            SystemPrompt = request.SystemPrompt ?? source.SystemPrompt,
            Schedule = source.Schedule,
            McpConfigJson = request.IncludeMcp ? source.McpConfigJson : null,
            // An explicit list (from the duplicate dialog's picker) wins; otherwise the
            // catalog servers follow the IncludeMcp choice like the inline config does.
            McpServerIds = request.McpServerIds
                ?? (request.IncludeMcp ? Deserialize<List<string>>(source.McpServerIdsJson) : new List<string>()),
            Agent = agent,
            AuthMode = authMode,
            OpenClawApiKeySource = agent == AgentKind.OpenClaw && authMode == AgentAuthMode.ApiKey
                ? request.OpenClawApiKeySource ?? source.OpenClawApiKeySource
                : null,
            Policy = request.Policy ?? DeserializeOptional<AgentPolicy>(source.AgentPolicyJson),
            // An explicit structured policy, including an empty default-deny policy,
            // supersedes legacy AllowedTools instead of rehydrating it later.
            AllowedTools = request.Policy is null ? Deserialize<List<string>>(source.AllowedToolsJson) : new List<string>(),
            Image = source.Image,
            RunAsRoot = source.RunAsRoot,
            AutoApprove = source.AutoApprove,
            Cpu = source.Cpu,
            Memory = source.Memory
        };
    }

    private static T Deserialize<T>(string? json) where T : new()
    {
        if (string.IsNullOrWhiteSpace(json)) return new T();
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) ?? new T(); }
        catch (System.Text.Json.JsonException) { return new T(); }
    }

    private static T? DeserializeOptional<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<T>(json,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

/// <summary>View of a running or scheduled session.</summary>
public record SessionInfo
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>What this agent is for (null = none set).</summary>
    public string? Description { get; init; }
    public required string Owner { get; init; }
    public string? ProjectId { get; init; }
    /// <summary>Optional parent session for orchestration (null = root session).</summary>
    public string? ParentSessionId { get; init; }
    public required SessionMode Mode { get; init; }
    /// <summary>UI rendering mode: "terminal" or "chat".</summary>
    public string UiMode { get; init; } = SessionUiMode.Terminal;
    /// <summary>First repo URL (backward-compatible display field).</summary>
    public string? RepoUrl { get; init; }
    public List<RepoRef> Repos { get; init; } = new();
    /// <summary>Whether the session has an MCP configuration.</summary>
    public bool HasMcp { get; init; }
    /// <summary>MCP config JSON (returned so the edit dialog can prefill it).</summary>
    public string? McpConfigJson { get; init; }
    /// <summary>Catalog MCP servers included in this session.</summary>
    public IReadOnlyList<string> McpServerIds { get; init; } = Array.Empty<string>();
    public required string Phase { get; init; }       // Pending | Running | Paused | Succeeded | Failed | Scheduled
    public string? PodIp { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? Prompt { get; init; }
    /// <summary>Extra system-prompt text this session runs with (null = none).</summary>
    public string? SystemPrompt { get; init; }
    /// <summary>
    /// Where a person opens this session in the web app, e.g.
    /// <c>https://agenthub.example.com/s/ab12cd</c> — what an API or MCP caller hands over when a
    /// human is meant to take the session on.
    ///
    /// Null when <c>FrontendOrigin</c> is not configured. Deliberately not derived from the
    /// request: the link is handed to a user, and a forwarded Host header would point them at
    /// whatever host the forwarder claimed to be.
    /// </summary>
    public string? Url { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = Array.Empty<string>();
    public AgentKind Agent { get; init; } = AgentKind.Claude;
    public AgentAuthMode AuthMode { get; init; } = AgentAuthMode.Auto;
    /// <summary>Which existing API key OpenClaw uses; set only for OpenClaw + ApiKey.</summary>
    public OpenClawApiKeySource? OpenClawApiKeySource { get; init; }
    /// <summary>The provider account this session is pinned to; null = the default account.</summary>
    public string? CredentialId { get; init; }
    public AgentPolicy Policy { get; init; } = new();
    public string? Schedule { get; init; }
    public bool QuestionPending { get; init; }
    /// <summary>A finished session with saved state can be resumed.</summary>
    public bool CanResume { get; init; }
    /// <summary>Custom image of the session (null = default agent image).</summary>
    public string? Image { get; init; }
    public bool RunAsRoot { get; init; }
    /// <summary>Tool permissions are approved automatically for this session.</summary>
    public bool AutoApprove { get; init; }
    public string Cpu { get; init; } = "500m";
    public string Memory { get; init; } = "1Gi";
    public BrowserSummary Browser { get; init; } = BrowserSummary.Stopped;
}

/// <summary>
/// Per-user credentials. These are written to a per-user Kubernetes secret
/// rather than being stored in plaintext in the database.
/// </summary>
public record UserCredentials
{
    public string? SshPrivateKey { get; init; }
    // Git personal access tokens are not part of this record. They live in a list keyed by host
    // (see GitPatStore) and have their own endpoints, because a merge-style PUT with one fixed
    // slot per provider cannot express "a second GitLab host".
    public string? AnthropicApiKey { get; init; }
    public string? OpenAiApiKey { get; init; }
    public string? CursorApiKey { get; init; }
    /// <summary>known_hosts entry of the git server (protects against MITM on the first clone).</summary>
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
    /// <summary>Stored git personal access tokens — kind and host only, never the token.</summary>
    public IReadOnlyList<GitPatInfo> GitPats { get; init; } = Array.Empty<GitPatInfo>();
    public bool AnthropicApiKey { get; init; }
    public bool GitKnownHosts { get; init; }
    public bool OpenAiApiKey { get; init; }
    public bool CursorApiKey { get; init; }
    public bool GitUserName { get; init; }
    public bool GitUserEmail { get; init; }
    public bool ClaudeSubscription { get; init; }
    public bool CodexSubscription { get; init; }
    public bool CursorSubscription { get; init; }
    public bool OpenclawSubscription { get; init; }
}

/// <summary>
/// One stored git personal access token as the API reports it. <c>Kind</c> is
/// <c>gitlab</c> or <c>github</c> (see <see cref="GitPatKind"/>); the token itself is never
/// included.
/// </summary>
public record GitPatInfo(string Id, string Kind, string Host);

/// <summary>The two kinds of PAT a session knows how to use; the kind decides the user part of
/// the store line and therefore which CLI (<c>glab</c> or <c>gh</c>) is configured.</summary>
public static class GitPatKind
{
    public const string GitLab = "gitlab";
    public const string GitHub = "github";

    public static bool IsValid(string? kind) => kind is GitLab or GitHub;
}

/// <summary>
/// Stores or rotates a PAT. The host is the identity: a second request for a host that is
/// already stored replaces that entry's token rather than adding a line, because git's store
/// helper answers with the first entry matching the host and a stale one in front would keep
/// winning after a rotation.
/// </summary>
public record UpsertGitPatRequest
{
    public string? Kind { get; init; }
    /// <summary>Hostname with an optional port, e.g. <c>gitlab.example.com</c>. Required: the
    /// token is only ever sent to this host, and defaulting to the public instance would be
    /// silently wrong for a self-hosted one.</summary>
    public string? Host { get; init; }
    public string? Token { get; init; }
}
