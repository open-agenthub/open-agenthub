using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AgentHub.Api.Mcp;

/// <summary>
/// The remote MCP tool surface. Mirrors the stdio server in <c>mcp/agenthub</c> so both speak
/// the same vocabulary, but resolves the caller from the OAuth access token instead of a
/// personal API token — every call runs as the user who approved the client, and
/// <see cref="ISessionService"/> enforces that user's ownership as it does for the REST API.
/// </summary>
[McpServerToolType]
public sealed class AgentHubMcpTools(
    IHttpContextAccessor http,
    ISessionService sessions,
    ILogger<AgentHubMcpTools> logger,
    ISessionMessageStore? messages = null,
    ISessionMessageDelivery? delivery = null)
{
    /// <summary>The calling user; see <see cref="McpCallerIdentity"/>.</summary>
    private string Owner => McpCallerIdentity.Owner(http);

    [McpServerTool(Name = "session_create")]
    [Description("Create and start an AgentHub session. Default mode is Interactive: tool requests "
                 + "outside its allow list wait for a person's approval. Use Autonomous only for "
                 + "unattended work, where they are approved automatically.")]
    public async Task<SessionInfo> CreateSession(
        [Description("Session title; also the agent name other agents address it by.")] string? title = null,
        [Description("What this agent is for.")] string? description = null,
        [Description("Initial prompt. Required for Autonomous and Scheduled sessions. An "
                     + "Interactive session starts working on it too and then stays live, so you "
                     + "can create a session with a task and hand its url to a person.")] string? prompt = null,
        [Description("Extra instructions appended to the agent's own system prompt — standing rules "
                     + "for the session, as opposed to the task. Supported on every agent; it is "
                     + "always appended, so it adds to the agent's instructions and never replaces "
                     + "them.")] string? systemPrompt = null,
        [Description("Interactive, Autonomous or Scheduled. Defaults to Interactive.")] string? mode = null,
        [Description("Claude, Codex, Cursor or OpenClaw.")] string? agent = null,
        [Description("Repository URL to clone into the workspace. For more than one repository, or "
                     + "to clone with a connected provider's credentials, use `repos`.")] string? repoUrl = null,
        [Description("Branch for repoUrl.")] string? repoBranch = null,
        [Description("Repositories to clone, as a JSON array: "
                     + "[{\"url\":\"https://host/org/thing.git\",\"branch\":\"main\","
                     + "\"providerId\":\"github\"}]. providerId names a Git provider this account "
                     + "has connected, and its OAuth token then authenticates the clone and any "
                     + "push; omit it for a public repository. One repository is checked out at "
                     + "/workspace/repo, several at /workspace/<name>. Takes precedence over "
                     + "repoUrl.")]
        string? repos = null,
        [Description("Project that groups the session.")] string? projectId = null,
        [Description("Parent session id for orchestration.")] string? parentSessionId = null,
        [Description("Cron expression; only for Scheduled sessions.")] string? schedule = null,
        [Description("CPU request, e.g. \"500m\". Lower it on a small cluster where the default "
                     + "would leave the pod unschedulable.")] string? cpu = null,
        [Description("Memory request, e.g. \"1Gi\".")] string? memory = null,
        [Description("\"true\" or \"false\". Approve tool-permission requests automatically. "
                     + "Defaults to on for Autonomous and Scheduled sessions, where nobody is "
                     + "watching to answer a prompt; pass false to keep such a session gated.")]
        string? autoApprove = null,
        [Description("\"true\" or \"false\". Run the container as root so the agent can install "
                     + "packages (apt, npm -g). Off by default: it gives up the read-only root "
                     + "filesystem, so only turn it on for a task that genuinely needs tooling the "
                     + "image does not ship. The pod stays unprivileged either way.")]
        string? runAsRoot = null,
        [Description("Delete the session on its own after this long, written with a unit: \"90m\", "
                     + "\"12h\", \"3d\" (5 minutes to 365 days). Omit to keep the session until "
                     + "somebody deletes it. The response's expiresAt says when it will go.")]
        string? autoDeleteAfter = null,
        [Description("What autoDeleteAfter counts from: \"lastActivity\" (default — anyone "
                     + "attaching, typing or messaging restarts the countdown) or \"start\". A "
                     + "Scheduled session only accepts \"start\".")]
        string? autoDeleteFrom = null,
        [Description("Id of the stored provider login (see credentials_list → accounts) a "
                     + "Subscription session mounts. Omit for the default account.")]
        string? credentialId = null,
        [Description("Comma-separated ids of the stored git personal access tokens (see "
                     + "credentials_list → gitPats) the session gets. Omit or \"*\" for every "
                     + "stored token; \"none\" for no token at all.")]
        string? gitPatIds = null,
        [Description("\"auto\" (default) or \"off\". With auto, a running Subscription session whose "
                     + "account hits its usage limit is moved to another available account of the "
                     + "same provider; off keeps it on its account.")]
        string? accountFailover = null,
        CancellationToken ct = default)
    {
        var request = new CreateSessionRequest
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
            Description = description,
            Prompt = prompt,
            SystemPrompt = systemPrompt,
            // Interactive, like the stdio and in-session servers: an Autonomous default
            // auto-approved every tool request of a session the caller merely forgot to scope,
            // and a person can still answer an interactive one through its url. Keep all three
            // consistent.
            Mode = ParseEnum(mode, SessionMode.Interactive),
            RepoUrl = repoUrl,
            RepoBranch = repoBranch,
            ProjectId = projectId,
            ParentSessionId = parentSessionId,
            Schedule = schedule,
            AutoApprove = ParseFlag(autoApprove),
            RunAsRoot = ParseFlag(runAsRoot) ?? false,
            AutoDeleteAfterSeconds = ParseAutoDeleteAfter(autoDeleteAfter),
            AutoDeleteFrom = autoDeleteFrom,
            CredentialId = string.IsNullOrWhiteSpace(credentialId) ? null : credentialId.Trim(),
            GitPatIds = ParseIdList(gitPatIds),
            AccountFailover = string.IsNullOrWhiteSpace(accountFailover) ? null : accountFailover.Trim()
        };
        if (ParseRepos(repos) is { Count: > 0 } parsedRepos) request = request with { Repos = parsedRepos };
        if (!string.IsNullOrWhiteSpace(agent)) request = request with { Agent = ParseEnum(agent, AgentKind.Claude) };
        // Only override the record's own defaults when a value was actually supplied; passing
        // null through would blank them and produce a pod spec with no resource request.
        if (!string.IsNullOrWhiteSpace(cpu)) request = request with { Cpu = cpu.Trim() };
        if (!string.IsNullOrWhiteSpace(memory)) request = request with { Memory = memory.Trim() };

        var created = await sessions.CreateSessionAsync(Owner, request, ct);
        logger.LogInformation("MCP client created session {SessionId} for {Owner}", created.Id, created.Owner);
        // The response carries `url`: the page a person opens to take this session over. It is null
        // on an instance with no FrontendOrigin configured — see SessionUrl.
        return created;
    }

    [McpServerTool(Name = "credentials_list")]
    [Description("List the credentials a session may use: provider logins (accounts, keyed by agent) "
                 + "with id, label and identity, stored git personal access tokens (id, kind, host — "
                 + "never the token), and which API keys are stored. Pass an account id as "
                 + "credentialId and git token ids as gitPatIds to session_create.")]
    public async Task<RemoteCredentialListing> ListCredentials(CancellationToken ct = default)
    {
        var owner = Owner;
        return RemoteCredentialListing.From(
            await sessions.ListProviderAccountsAsync(owner, ct),
            await sessions.GetCredentialStatusAsync(owner, ct));
    }

    [McpServerTool(Name = "account_status")]
    [Description("Which provider account a session runs on, whether that account is at its usage limit "
                 + "(isExhausted, exhaustedUntil), the session's accountFailover setting, and the other "
                 + "stored accounts of the same provider as alternatives. Pass an alternative's id to "
                 + "account_switch to move a running session.")]
    public async Task<AccountStatus> GetAccountStatus([Description("Session id.")] string sessionId, CancellationToken ct = default)
    {
        var owner = Owner;
        var session = await sessions.GetSessionAsync(owner, sessionId, ct) ?? throw new McpException("session_not_found");
        return AccountStatus.From(session, await sessions.ListProviderAccountsAsync(owner, ct));
    }

    [McpServerTool(Name = "account_switch")]
    [Description("Move a running Subscription session to another stored provider account (an id from "
                 + "account_status or credentials_list). The agent restarts with the other login and "
                 + "resumes its conversation. Fails with session_not_running when the session has no "
                 + "live pod (set credentialId with the edit flow for the next start instead), "
                 + "invalid_argument for an unknown account or an API-key session, and pod_refused "
                 + "when the pod did not take the file.")]
    public async Task<SessionInfo> SwitchAccount(
        [Description("Session id.")] string sessionId,
        [Description("The account to switch to.")] string credentialId,
        CancellationToken ct = default)
    {
        try
        {
            var switched = await sessions.SwitchSessionCredentialAsync(Owner, sessionId, credentialId?.Trim() ?? "", ct);
            logger.LogInformation("MCP client switched session {SessionId} to account {Account}", sessionId, switched.CredentialId);
            return switched;
        }
        catch (KeyNotFoundException) { throw new McpException("session_not_found"); }
        catch (ArgumentException e) { throw new McpException("invalid_argument: " + e.Message); }
        catch (InvalidOperationException e) { throw new McpException("session_not_running: " + e.Message); }
        catch (HttpRequestException e) { throw new McpException("pod_refused: " + e.Message); }
    }

    [McpServerTool(Name = "session_get")]
    [Description("Get one of your sessions by id.")]
    public async Task<SessionInfo> GetSession([Description("Session id.")] string id, CancellationToken ct = default)
        => await sessions.GetSessionAsync(Owner, id, ct) ?? throw new McpException("session_not_found");

    [McpServerTool(Name = "session_logs")]
    [Description("Read a session's transcript: the conversation as the agent recorded it (user, "
                 + "assistant, tool turns), or the cleaned terminal output for sessions without "
                 + "one. This is the only place its actual output lives: the pod log shows just "
                 + "the launch command, and a finished session's pod is gone. Returns the tail by default.")]
    public async Task<string> GetSessionLogs(
        [Description("Session id.")] string id,
        [Description("Return at most this many characters from the end. Default 20000, max 200000. "
                     + "Pass 0 for the whole transcript.")] int? maxChars = null,
        CancellationToken ct = default)
    {
        var transcript = await SessionTranscripts.ReadableAsync(sessions, Owner, id, ct)
                         ?? throw new McpException("session_not_found");
        if (transcript.Length == 0) return "(the session has not written any output yet)";

        var limit = maxChars ?? 20_000;
        // A transcript of a long session runs to megabytes; returning it whole would blow up the
        // caller's context for the sake of a few lines at the end, which is the part that says
        // how the session finished.
        if (limit <= 0 || transcript.Length <= limit) return transcript;
        limit = Math.Min(limit, 200_000);
        return "… truncated, showing the last " + limit + " characters …\n"
               + transcript[^limit..];
    }

    [McpServerTool(Name = "session_transcript")]
    [Description("Poll a session's transcript for what is new. Pass the previous call's nextOffset "
                 + "as offset and only the output since then comes back, so following a long "
                 + "session does not mean re-reading megabytes. `running` is false once the session "
                 + "has finished — that is when to stop polling. Prefer this over session_logs when "
                 + "you are watching a session you started.")]
    public async Task<TranscriptPage> GetSessionTranscript(
        [Description("Session id.")] string id,
        [Description("Start here. Use the previous response's nextOffset; omit to start at 0.")]
        int? offset = null,
        [Description("Return at most this many characters. Default 100000, max 1000000.")]
        int? maxChars = null,
        CancellationToken ct = default)
    {
        // Phase before text, so a session that finishes mid-call is reported as still running with
        // its final output already present — one extra poll, rather than output arriving after a
        // "finished" the caller already acted on.
        var session = await sessions.GetSessionAsync(Owner, id, ct)
                      ?? throw new McpException("session_not_found");
        var transcript = await SessionTranscripts.ReadableAsync(sessions, Owner, id, ct)
                         ?? throw new McpException("session_not_found");
        return TranscriptPage.From(session.Id, session.Phase, transcript, offset, maxChars);
    }

    [McpServerTool(Name = "session_list")]
    [Description("List your sessions. Optionally filter by parentSessionId and/or phase.")]
    public async Task<IReadOnlyList<SessionInfo>> ListSessions(
        [Description("Only sessions with this parent.")] string? parentSessionId = null,
        [Description("Pending, Running, Paused, Succeeded, Failed or Scheduled.")] string? phase = null,
        CancellationToken ct = default)
    {
        var all = await sessions.ListSessionsAsync(Owner, ct);
        return all.Where(s =>
                (parentSessionId is null || s.ParentSessionId == parentSessionId) &&
                (phase is null || string.Equals(s.Phase, phase, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    [McpServerTool(Name = "session_wait")]
    [Description("Poll a session until it reaches Succeeded or Failed, or until the timeout expires.")]
    public async Task<SessionInfo> WaitForSession(
        [Description("Session id.")] string id,
        [Description("Give up after this many milliseconds. Default 600000, max 86400000.")] int? timeoutMs = null,
        [Description("Poll interval in milliseconds. Default 5000, max 60000.")] int? intervalMs = null,
        CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs ?? 600_000, 1, 86_400_000));
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(intervalMs ?? 5_000, 1, 60_000));
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            var session = await sessions.GetSessionAsync(Owner, id, ct) ?? throw new McpException("session_not_found");
            if (session.Phase is "Succeeded" or "Failed") return session;
            if (DateTimeOffset.UtcNow >= deadline) throw new McpException("session_wait_timeout");
            await Task.Delay(interval, ct);
        }
    }

    [McpServerTool(Name = "session_convert")]
    [Description("Continue a finished or paused Autonomous session as an Interactive one, so a person "
                 + "can take the conversation on. Claude and Codex keep the conversation; Cursor and "
                 + "OpenClaw start a new one in the same workspace. The session resumes right away "
                 + "unless resume is \"false\". Fails with session_not_convertible while the session "
                 + "is running or is not Autonomous.")]
    public async Task<SessionInfo> ConvertSession(
        [Description("Session id.")] string sessionId,
        [Description("\"terminal\" (default) or \"chat\" (interactive Claude only).")] string? uiMode = null,
        [Description("\"true\" or \"false\". Keep approving tool requests automatically. Off by "
                     + "default: a person is now there to answer them.")] string? autoApprove = null,
        [Description("\"true\" (default) or \"false\". False only changes the mode and leaves the "
                     + "session stopped.")] string? resume = null,
        CancellationToken ct = default)
    {
        var request = new ConvertSessionRequest
        {
            UiMode = uiMode,
            AutoApprove = ParseFlag(autoApprove),
            Resume = ParseFlag(resume) ?? true
        };
        try
        {
            var converted = await sessions.ConvertSessionAsync(Owner, sessionId, request, ct);
            logger.LogInformation("MCP client converted session {SessionId} to interactive", sessionId);
            return converted;
        }
        catch (KeyNotFoundException) { throw new McpException("session_not_found"); }
        catch (InvalidOperationException e) { throw new McpException("session_not_convertible: " + e.Message); }
        catch (ArgumentException e) { throw new McpException("invalid_argument: " + e.Message); }
    }

    [McpServerTool(Name = "session_delete")]
    [Description("Delete a session, removing its pod and record. Does not cascade to child sessions.")]
    public async Task<string> DeleteSession([Description("Session id.")] string id, CancellationToken ct = default)
    {
        try
        {
            await sessions.DeleteSessionAsync(Owner, id, ct);
            return id;
        }
        catch (KeyNotFoundException) { throw new McpException("session_not_found"); }
    }

    [McpServerTool(Name = "agents_list")]
    [Description("List your agents (sessions) with title, description and phase, optionally scoped to one project.")]
    public async Task<IReadOnlyList<AgentSummary>> ListAgents(
        [Description("Only agents in this project.")] string? projectId = null, CancellationToken ct = default)
    {
        var all = await sessions.ListSessionsAsync(Owner, ct);
        return all.Where(s => projectId is null || s.ProjectId == projectId)
            .Select(s => new AgentSummary(s.Id, s.Title, s.Description, s.Phase, s.ProjectId))
            .ToList();
    }

    [McpServerTool(Name = "agent_send")]
    [Description("Send a message or task to one of your agents. \"to\" is a session id or a unique title; "
                 + "scope the lookup with projectId. By default the agent reads it via its in-session "
                 + "agent_inbox tool; with priority the message is pushed straight into the running agent's "
                 + "prompt. deliveredVia in the result says where it went: inbox, injected or mod.")]
    public async Task<AgentMessageResult> SendToAgent(
        [Description("Session id or unique agent title.")] string to,
        [Description("Message body, 1..4000 characters.")] string message,
        [Description("Narrows an ambiguous title to one project.")] string? projectId = null,
        [Description("\"true\" to deliver into the running agent's prompt now instead of its inbox.")]
        string? priority = null,
        [Description("\"true\" to stop the agent's current work before delivering; implies priority.")]
        string? interrupt = null,
        CancellationToken ct = default)
    {
        if (messages is null) throw new McpException("messaging_unavailable");

        var body = AgentMessaging.NormalizeBody(message)
                   ?? throw new McpException("message_body_invalid");

        var owner = Owner;
        var candidates = (await sessions.ListSessionsAsync(owner, ct))
            .Where(s => projectId is null || s.ProjectId == projectId)
            .ToList();

        // An id always wins over a title, so an agent named after another agent's id cannot
        // shadow it.
        var target = candidates.FirstOrDefault(s => s.Id == to);
        if (target is null)
        {
            var byTitle = candidates.Where(s => string.Equals(s.Title, to, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byTitle.Count == 0) throw new McpException("agent_not_found");
            // Silently picking one would deliver the task to the wrong agent; name the options.
            if (byTitle.Count > 1)
                throw new McpException("agent_title_ambiguous: " + string.Join(", ", byTitle.Select(s => s.Id)));
            target = byTitle[0];
        }

        var (isPriority, isInterrupt) = AgentMessaging.ResolveFlags(ParseFlag(priority), ParseFlag(interrupt));
        var record = new SessionMessageRecord
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            ProjectId = target.ProjectId,
            FromSessionId = null,
            ToSessionId = target.Id,
            Owner = owner,
            Body = body,
            Priority = isPriority,
            Interrupt = isInterrupt
        };
        await messages.AddAsync(record, ct);
        var sent = await AgentMessageDispatch.PushAsync(delivery, target, record, null, ct);
        return new AgentMessageResult(record.Id, target.Id, target.Title, sent.Via, sent.Reason);
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;

    /// <summary>
    /// A duration with a unit rather than a number of seconds: the caller is a language model,
    /// and "12h" is what it writes. Reported rather than ignored when unreadable, because a
    /// session silently created without the deadline it asked for is a session that is never
    /// cleaned up (docs/session-expiry.md).
    /// </summary>
    private static int? ParseAutoDeleteAfter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return SessionExpiry.ParseDuration(text); }
        catch (ArgumentException e) { throw new McpException(e.Message); }
    }

    /// <summary>
    /// Reads the repository list, which arrives as JSON text for the same reason the boolean flags
    /// do — see <see cref="ParseFlag"/>. A declared array type would make every already-connected
    /// client's call fail until it reconnected.
    ///
    /// Malformed JSON is reported rather than ignored: silently creating a session with no
    /// repository would leave the agent looking at an empty workspace and the caller wondering why.
    /// Everything about the entries themselves — count, URL shape, whether the provider is actually
    /// connected — is checked by the session service, so the rules cannot drift between the two
    /// MCP servers and the REST API.
    /// </summary>
    private static List<RepoRef>? ParseRepos(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<List<RepoRef>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException e)
        {
            throw new McpException($"repos is not a valid JSON array of repositories: {e.Message}");
        }
    }

    /// <summary>
    /// Reads a boolean flag that arrives as text. These are declared as strings rather than bools
    /// on purpose: an MCP client caches the tool schema when it connects, so a parameter added
    /// afterwards is sent as a string by every already-connected client. Declaring bool made the
    /// whole call fail with "The JSON value could not be converted to System.Nullable`1[Boolean]"
    /// until the client reconnected — an unhelpful error for something the caller got right.
    ///
    /// Null or unrecognised means "not specified", which lets the mode decide.
    /// </summary>
    private static bool? ParseFlag(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "1" or "on" => true,
        "false" or "no" or "0" or "off" => false,
        _ => null
    };

    /// <summary>
    /// Reads an id list that arrives as text, for the reason given at <see cref="ParseFlag"/>.
    /// Empty or <c>*</c> is "not specified" (every stored token, the session service's default);
    /// <c>none</c> is the one way to ask for no token, since an empty string cannot carry it.
    /// </summary>
    public static List<string>? ParseIdList(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text == "*") return null;
        if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase)) return new List<string>();
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToList();
    }
}

public record AgentSummary(string Id, string Title, string? Description, string Phase, string? ProjectId);

public record AgentMessageResult(string Id, string To, string Title, string DeliveredVia = MessageDeliveryVia.Inbox,
    string? Reason = null);
