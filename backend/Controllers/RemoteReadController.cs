using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Library;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHub.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/remote")]
public sealed class RemoteReadController : ControllerBase
{
    private readonly Func<string, CancellationToken, Task<RemoteCaller?>> _findCaller;
    private readonly ISessionService _sessions;
    private readonly IProjectStore _projects;
    private readonly ILibraryAccess _library;
    private readonly ISessionMessageStore _messages;
    private readonly ISessionAccessService _access;
    private readonly ILiveTranscriptReader _live;

    [ActivatorUtilitiesConstructor]
    public RemoteReadController(ApiTokenStore tokens, ISessionService sessions, IProjectStore projects,
        ILibraryAccess library, ISessionMessageStore messages, ISessionAccessService access, ILiveTranscriptReader live)
        : this(tokens.FindCallerByTokenAsync, sessions, projects, library, messages, access, live) { }

    public RemoteReadController(Func<string, CancellationToken, Task<RemoteCaller?>> findCaller,
        ISessionService sessions, IProjectStore projects, ILibraryAccess library,
        ISessionMessageStore messages, ISessionAccessService access, ILiveTranscriptReader live)
    {
        _findCaller = findCaller;
        _sessions = sessions;
        _projects = projects;
        _library = library;
        _messages = messages;
        _access = access;
        _live = live;
    }

    private Task<RemoteCaller?> CallerAsync(CancellationToken ct)
        => RemoteBearerToken.Read(Request) is { } token
            ? _findCaller(token, ct) : Task.FromResult<RemoteCaller?>(null);

    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyList<ProjectInfo>>> Projects(CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        if (caller is null) return Unauthorized();
        return Ok(await _projects.ListAsync(caller.Owner, ct));
    }

    [HttpGet("projects/{id}")]
    public async Task<ActionResult<ProjectInfo>> Project(string id, CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        if (caller is null) return Unauthorized();
        return await _projects.GetAsync(caller.Owner, id, ct) is { } project ? Ok(project) : NotFound();
    }

    [HttpGet("mcp-servers")]
    public async Task<ActionResult<IReadOnlyList<RemoteMcpServerInfo>>> McpServers(CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        if (caller is null) return Unauthorized();
        // The library service owns the licence/share policy; projecting only identifiers keeps
        // configuration, headers and secrets off the token discovery surface even for own items.
        var records = await _library.ListMcpServersAsync(caller.Owner, ct);
        return Ok(records.Select(r => new RemoteMcpServerInfo(r.Id, r.Name)).ToList());
    }

    [HttpGet("sessions/{id}/conversation")]
    public async Task<ActionResult<ConversationPage>> Conversation(
        string id, [FromQuery] int? offset, [FromQuery] int? max, CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        if (caller is null) return Unauthorized();
        var session = await _sessions.GetSessionAsync(caller.Owner, id, ct);
        if (session is not null)
        {
            var page = await SessionTranscripts.PageAsync(_sessions, caller.Owner, id, session.Phase,
                offset, max, ct, token => _live.ReadAsync(session, token));
            return Ok(page);
        }
        // Only the existing user-share resolver can cross ownership. A personal token is not
        // a share-link token, and a revoked grant or lapsed licence must fail as on the web path.
        var shared = await _access.ResolveUserAsync(caller.Owner, id, ct);
        if (shared is null) return NotFound();
        var sharedPage = await SessionTranscripts.PageAsync(_sessions, shared.Session.Owner, id,
            shared.Session.Status, offset, max, ct);
        return Ok(sharedPage);
    }

    [HttpGet("sessions/{id}/messages")]
    public async Task<ActionResult<RemoteMessagePage>> Messages(
        string id, [FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken ct)
    {
        var caller = await CallerAsync(ct);
        if (caller is null) return Unauthorized();
        if (await _sessions.GetSessionAsync(caller.Owner, id, ct) is null) return NotFound();
        if (limit is < 1 or > 100 || !MessagePageCursor.TryParse(cursor, out var before))
            return BadRequest("invalid_message_page");
        var size = limit ?? 20;
        var rows = await _messages.ListPageAsync(caller.Owner, id, before, size + 1, ct);
        var recent = rows.Take(size).ToList();
        var titles = recent.Any(m => m.FromSessionId is not null)
            ? (await _sessions.ListSessionsAsync(caller.Owner, ct)).ToDictionary(s => s.Id, s => s.Title)
            : new Dictionary<string, string>();
        var messages = recent.Select(m => new AgentMessageInfo(m.Id, m.FromSessionId,
            m.FromSessionId is null ? null : titles.GetValueOrDefault(m.FromSessionId),
            m.Body, m.CreatedAt, m.DeliveredAt, m.Priority, m.Interrupt, m.DeliveredVia)).ToList();
        return Ok(new RemoteMessagePage(messages, rows.Count > size
            ? MessagePageCursor.Encode(recent[^1]) : null));
    }
}

public sealed record RemoteMcpServerInfo(string Id, string Name);
public sealed record RemoteMessagePage(IReadOnlyList<AgentMessageInfo> Messages, string? NextCursor);
