using System.ComponentModel;
using System.Security.Claims;
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
    ISessionMessageStore? messages = null)
{
    /// <summary>
    /// The calling user. The access token carries "preferred_username", the same claim the REST
    /// API keys ownership on.
    /// </summary>
    private string Owner
    {
        get
        {
            var user = http.HttpContext?.User;
            var name = user?.FindFirstValue("preferred_username")
                       ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? user?.Identity?.Name;
            // Reaching a tool without an identity would mean the endpoint was mapped without its
            // authorization policy; refuse rather than silently acting as somebody.
            return string.IsNullOrWhiteSpace(name)
                ? throw new McpException("unauthenticated")
                : name;
        }
    }

    [McpServerTool(Name = "session_create")]
    [Description("Create and start an AgentHub session. Default mode is Autonomous.")]
    public async Task<SessionInfo> CreateSession(
        [Description("Session title; also the agent name other agents address it by.")] string? title = null,
        [Description("What this agent is for.")] string? description = null,
        [Description("Initial prompt. Required for Autonomous and Scheduled sessions.")] string? prompt = null,
        [Description("Interactive, Autonomous or Scheduled. Defaults to Autonomous.")] string? mode = null,
        [Description("Claude, Codex, Cursor or OpenClaw.")] string? agent = null,
        [Description("Repository URL to clone into the workspace.")] string? repoUrl = null,
        [Description("Branch for repoUrl.")] string? repoBranch = null,
        [Description("Project that groups the session.")] string? projectId = null,
        [Description("Parent session id for orchestration.")] string? parentSessionId = null,
        [Description("Cron expression; only for Scheduled sessions.")] string? schedule = null,
        CancellationToken ct = default)
    {
        var request = new CreateSessionRequest
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
            Description = description,
            Prompt = prompt,
            // The stdio server defaults to Autonomous, because a caller driving sessions through
            // a tool has no terminal to interact with. Keep both servers consistent.
            Mode = ParseEnum(mode, SessionMode.Autonomous),
            RepoUrl = repoUrl,
            RepoBranch = repoBranch,
            ProjectId = projectId,
            ParentSessionId = parentSessionId,
            Schedule = schedule
        };
        if (!string.IsNullOrWhiteSpace(agent)) request = request with { Agent = ParseEnum(agent, AgentKind.Claude) };

        var created = await sessions.CreateSessionAsync(Owner, request, ct);
        logger.LogInformation("MCP client created session {SessionId} for {Owner}", created.Id, created.Owner);
        return created;
    }

    [McpServerTool(Name = "session_get")]
    [Description("Get one of your sessions by id.")]
    public async Task<SessionInfo> GetSession([Description("Session id.")] string id, CancellationToken ct = default)
        => await sessions.GetSessionAsync(Owner, id, ct) ?? throw new McpException("session_not_found");

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
                 + "scope the lookup with projectId. The agent reads it via its in-session agent_inbox tool.")]
    public async Task<AgentMessageResult> SendToAgent(
        [Description("Session id or unique agent title.")] string to,
        [Description("Message body, 1..4000 characters.")] string message,
        [Description("Narrows an ambiguous title to one project.")] string? projectId = null,
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

        var record = new SessionMessageRecord
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            ProjectId = target.ProjectId,
            FromSessionId = null,
            ToSessionId = target.Id,
            Owner = owner,
            Body = body
        };
        await messages.AddAsync(record, ct);
        return new AgentMessageResult(record.Id, target.Id, target.Title);
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}

public record AgentSummary(string Id, string Title, string? Description, string Phase, string? ProjectId);

public record AgentMessageResult(string Id, string To, string Title);
