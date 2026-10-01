using AgentHub.Api.Agents;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Remote session API authenticated by a personal API token (see
/// <see cref="ApiTokensController"/>) rather than an interactive login.
/// Auth is handled manually here via the Authorization: Bearer header, so the
/// global authentication pipeline is left untouched.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/remote")]
public sealed class RemoteController : ControllerBase
{
    private readonly Func<string, CancellationToken, Task<string?>> _findOwner;
    private readonly ISessionService _svc;
    private readonly ISessionMessageStore? _messages;

    public RemoteController(ApiTokenStore tokens, ISessionService svc, ISessionMessageStore? messages = null)
        : this(tokens.FindOwnerByTokenAsync, svc, messages) { }

    /// <summary>Test seam: resolve owner from a plaintext token without Postgres.</summary>
    public RemoteController(Func<string, CancellationToken, Task<string?>> findOwnerByToken, ISessionService svc,
        ISessionMessageStore? messages = null)
    {
        _findOwner = findOwnerByToken;
        _svc = svc;
        _messages = messages;
    }

    /// <summary>Resolves the bearer token to its owner, or null if missing/invalid.</summary>
    private async Task<string?> ResolveOwnerAsync(CancellationToken ct)
    {
        var header = Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var token = header[scheme.Length..].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith("oah_", StringComparison.Ordinal)) return null;

        return await _findOwner(token, ct);
    }

    [HttpPost("sessions")]
    public async Task<ActionResult<SessionInfo>> Create([FromBody] CreateSessionRequest req, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try { return Ok(await _svc.CreateSessionAsync(owner, req, ct)); }
        catch (AgentNotAllowedException e) { return StatusCode(StatusCodes.Status403Forbidden, e.Message); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (SessionLimitExceededException e) { return StatusCode(StatusCodes.Status429TooManyRequests, e.Message); }
    }

    [HttpGet("sessions/{id}")]
    public async Task<ActionResult<SessionInfo>> Get(string id, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        return await _svc.GetSessionAsync(owner, id, ct) is { } s ? Ok(s) : NotFound();
    }

    /// <summary>
    /// One page of the session transcript, for a caller following a session it created without
    /// holding a websocket open.
    ///
    /// <paramref name="offset"/> is a cursor: pass back the <c>nextOffset</c> of the previous page
    /// and only new output comes over the wire. Re-fetching from zero each time would mean
    /// downloading a transcript that grows into the megabytes to read the few lines that changed.
    /// <c>running</c> says whether to poll again.
    /// </summary>
    [HttpGet("sessions/{id}/transcript")]
    public async Task<ActionResult<TranscriptPage>> Transcript(
        string id, [FromQuery] int? offset, [FromQuery] int? maxChars, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        // The phase is read first: a session that finishes between the two reads is then reported
        // as still running with the final output already included, so the caller polls once more
        // and sees the terminal phase. The other order could report "finished" with output missing.
        var session = await _svc.GetSessionAsync(owner, id, ct);
        if (session is null) return NotFound();
        var transcript = await _svc.GetTranscriptAsync(owner, id, ct);
        if (transcript is null) return NotFound();
        return Ok(TranscriptPage.From(session.Id, session.Phase, transcript, offset, maxChars));
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<SessionInfo>>> List(CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        return Ok(await _svc.ListSessionsAsync(owner, ct));
    }

    /// <summary>
    /// Sends a message/task to one of the token owner's sessions. Stored with a null
    /// sender session — the receiving agent sees it as an external message from its owner.
    /// </summary>
    [HttpPost("sessions/{id}/messages")]
    public async Task<IActionResult> SendMessage(string id, [FromBody] RemoteAgentMessageRequest req, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        if (_messages is null) return NotFound();

        var text = AgentMessaging.NormalizeBody(req.Body);
        if (text is null)
            return BadRequest($"A message body of 1..{AgentMessaging.MaxBodyChars} characters is required.");
        var target = await _svc.GetSessionAsync(owner, id, ct);
        if (target is null) return NotFound();

        var message = new SessionMessageRecord
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            ProjectId = target.ProjectId,
            FromSessionId = null,
            ToSessionId = target.Id,
            Owner = owner,
            Body = text
        };
        await _messages.AddAsync(message, ct);
        return Ok(new { id = message.Id, to = target.Id });
    }

    [HttpDelete("sessions/{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try
        {
            await _svc.DeleteSessionAsync(owner, id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
