using AgentHub.Api.Agents;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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
    private readonly Func<string, CancellationToken, Task<RemoteCaller?>> _findCaller;
    private readonly ISessionService _svc;
    private readonly ISessionMessageStore? _messages;

    /// <remarks>
    /// Marked as the one to construct from the container. MVC builds a controller through
    /// <see cref="ActivatorUtilities"/> with no explicit arguments, and the test seam below is
    /// just as good a match: without the attribute the choice is ambiguous and activation throws
    /// before any action runs, so every route on this controller answers 500 — including an
    /// unauthenticated one, which makes it look like an auth problem rather than a wiring one.
    /// </remarks>
    [ActivatorUtilitiesConstructor]
    public RemoteController(ApiTokenStore tokens, ISessionService svc, ISessionMessageStore? messages = null)
        : this(tokens.FindCallerByTokenAsync, svc, messages) { }

    /// <summary>Test seam: resolve the caller (owner and scope) from a plaintext token without Postgres.</summary>
    public RemoteController(Func<string, CancellationToken, Task<RemoteCaller?>> findCallerByToken, ISessionService svc,
        ISessionMessageStore? messages = null)
    {
        _findCaller = findCallerByToken;
        _svc = svc;
        _messages = messages;
    }

    /// <summary>Resolves the bearer token to its owner and scope, or null if missing/invalid.</summary>
    private async Task<RemoteCaller?> ResolveCallerAsync(CancellationToken ct)
    {
        var header = Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var token = header[scheme.Length..].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith("oah_", StringComparison.Ordinal)) return null;

        return await _findCaller(token, ct);
    }

    /// <summary>The owner alone, for the endpoints that act on existing sessions. Only session
    /// creation and the credential listing consult the scope: a session belongs to its owner,
    /// not to the token that created it (docs/credential-scopes.md).</summary>
    private async Task<string?> ResolveOwnerAsync(CancellationToken ct) => (await ResolveCallerAsync(ct))?.Owner;

    [HttpPost("sessions")]
    public async Task<ActionResult<SessionInfo>> Create([FromBody] CreateSessionRequest req, CancellationToken ct)
    {
        var caller = await ResolveCallerAsync(ct);
        if (caller is null) return Unauthorized();
        try
        {
            if (caller.Scope is { } scope)
            {
                // Narrowed before the service sees it; the service still validates the ids.
                req = CredentialScope.ApplyToCreate(req, scope,
                    await _svc.ListProviderAccountsAsync(caller.Owner, ct),
                    (await _svc.GetCredentialStatusAsync(caller.Owner, ct)).GitPats);
            }
            return Ok(await _svc.CreateSessionAsync(caller.Owner, req, ct));
        }
        catch (CredentialScopeException e) { return StatusCode(StatusCodes.Status403Forbidden, e.Code); }
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
        var transcript = await SessionTranscripts.ReadableAsync(_svc, owner, id, ct);
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
    /// The provider accounts, git PATs and API keys a session created with this token may use —
    /// ids, labels and hosts, never a secret. The in-app listing sits behind the interactive login,
    /// which a token cannot pass, and without this a caller had to guess a <c>credentialId</c>.
    /// </summary>
    [HttpGet("credentials")]
    public async Task<ActionResult<RemoteCredentialListing>> Credentials(CancellationToken ct)
    {
        var caller = await ResolveCallerAsync(ct);
        if (caller is null) return Unauthorized();
        var listing = RemoteCredentialListing.From(
            await _svc.ListProviderAccountsAsync(caller.Owner, ct),
            await _svc.GetCredentialStatusAsync(caller.Owner, ct));
        return Ok(CredentialScope.FilterListing(listing, caller.Scope));
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

    /// <summary>
    /// Pauses a session: the pod is removed after uploading its state, and the session stays
    /// resumable. Needed on this surface because a client that takes a conversation off the
    /// cluster has to stop the pod first — otherwise the agent keeps working on the same
    /// conversation and keeps overwriting the archive, and one of the two branches is lost at
    /// the next upload.
    /// </summary>
    [HttpPost("sessions/{id}/pause")]
    public async Task<ActionResult<SessionInfo>> Pause(string id, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try { return Ok(await _svc.PauseSessionAsync(owner, id, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
    }

    /// <summary>Starts a paused or finished session again from its saved state — the step that
    /// hands a conversation back after it continued elsewhere.</summary>
    [HttpPost("sessions/{id}/resume")]
    public async Task<ActionResult<SessionInfo>> Resume(string id, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try { return Ok(await _svc.ResumeSessionAsync(owner, id, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (AgentNotAllowedException e) { return StatusCode(StatusCodes.Status403Forbidden, e.Message); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (InvalidOperationException e) { return Conflict(e.Message); }
    }

    /// <summary>
    /// The session's provider state archive, so a client outside the cluster can continue the
    /// conversation in its own agent CLI. Same archive the resume path unpacks into the pod.
    /// </summary>
    [HttpGet("sessions/{id}/state")]
    public async Task<IActionResult> DownloadState(string id, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        if (await _svc.OpenStateArchiveAsync(owner, id, ct) is not { } archive) return NotFound();
        return File(archive, "application/gzip", $"{id}-state.tgz");
    }

    /// <summary>Hands a conversation that continued elsewhere back to the session: the next
    /// resume unpacks this archive instead of the one the pod left behind.</summary>
    [HttpPut("sessions/{id}/state")]
    [RequestSizeLimit(SessionStateTransfer.MaxArchiveBytes)]
    public async Task<IActionResult> UploadState(string id, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try
        {
            return await _svc.ReplaceStateArchiveAsync(owner, id, Request.Body, Request.ContentLength, ct)
                ? NoContent()
                : StatusCode(StatusCodes.Status503ServiceUnavailable,
                    "Object storage is not configured, so session state cannot be stored.");
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException e) { return Conflict(e.Message); }
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
