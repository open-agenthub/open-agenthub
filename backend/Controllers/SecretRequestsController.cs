using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// The owner's side of a secret request (docs/session-secrets.md): what the agents asked for, and
/// the answer — a secret granted to that one session, or a refusal. Since #330 the answer form
/// sends the value here, so that storing it and granting it succeed or fail together; it goes to
/// the store and never into a response or a log.
/// </summary>
[ApiController]
[Authorize]
[Route("api/secret-requests")]
public sealed class SecretRequestsController(ISecretRequestStore requests, ISessionService sessions, ISessionStore records)
    : ControllerBase
{
    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    public sealed record SecretRequestInfo(string Id, string SessionId, string? SessionTitle, string Name, string Delivery,
        string? Purpose, string Status, DateTime CreatedAt);

    // The title from the stored record rather than the session service, whose lookup also asks
    // the API server for the pod — once per request in a list.
    private async Task<SecretRequestInfo> InfoAsync(SecretRequest r, CancellationToken ct) =>
        new(r.Id, r.SessionId, (await records.GetAsync(Owner, r.SessionId, ct))?.Title, r.Name, r.Delivery,
            r.Purpose, r.Status, r.CreatedAt);

    /// <summary>Open requests of all the owner's sessions, or of one.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<SecretRequestInfo>> List([FromQuery] string? sessionId, CancellationToken ct)
    {
        var open = await requests.ListPendingAsync(Owner, string.IsNullOrWhiteSpace(sessionId) ? null : sessionId, ct);
        var list = new List<SecretRequestInfo>();
        foreach (var request in open) list.Add(await InfoAsync(request, ct));
        return list;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<SecretRequestInfo>> Get(string id, CancellationToken ct)
        => await requests.GetAsync(id, ct) is { } r && r.Owner == Owner ? Ok(await InfoAsync(r, ct)) : NotFound();

    /// <summary>
    /// Answers the request: grants a secret to the requesting session and closes the request, also
    /// for a session created by a restricted token or by another session (#330). With
    /// <c>secret</c> the value is stored in the same call. 400 when the hub refuses the grant, and
    /// then nothing the call stored stays stored; 409 when the request was closed in the meantime.
    /// </summary>
    [HttpPost("{id}/provide")]
    public async Task<ActionResult<SessionSecretGrant>> Provide(string id, [FromBody] ProvideSecretRequestBody body, CancellationToken ct)
    {
        if (await requests.GetAsync(id, ct) is not { } request || request.Owner != Owner) return NotFound();
        if (request.Status != SecretRequest.Pending) return Conflict(new { error = "request_closed", status = request.Status });
        var existingId = (body.Secret?.Id ?? body.SecretId)?.Trim();
        if (body.Secret is null && string.IsNullOrWhiteSpace(existingId)) return BadRequest("secretId or secret is required.");
        SessionSecretGrant grant;
        string? created = null;
        try
        {
            // Store and grant used to be two calls from the page, and a refused grant left the
            // typed value stored with nothing pointing at it. A stored secret is granted first and
            // only then given the new value; a new one has to exist before it can be granted, so it
            // is removed again if the grant is refused.
            // A stored secret whose new name or value the store then refuses keeps the grant, the
            // same as answering with it unchanged, and the request stays open for another try.
            if (!string.IsNullOrWhiteSpace(existingId))
            {
                grant = await sessions.GrantSessionSecretAsync(Owner, request.SessionId, existingId, ct);
                if (body.Secret is { } update) await sessions.UpsertSessionSecretAsync(Owner, update with { Id = existingId }, ct);
            }
            else
            {
                created = (await sessions.UpsertSessionSecretAsync(Owner, body.Secret!, ct)).Id;
                grant = await sessions.GrantSessionSecretAsync(Owner, request.SessionId, created, ct);
            }
        }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException)
        {
            if (created is not null) await RemoveUngrantedAsync(created);
            return e is KeyNotFoundException ? NotFound() : BadRequest(e.Message);
        }
        // The grant stands even when the tool gave up a moment ago: the owner meant to give the
        // session this secret, and the next request for it is then answered at once.
        if (!await requests.ResolveAsync(id, SecretRequest.Provided, grant.SecretId, ct))
            return Conflict(new { error = "request_closed" });
        await ClearQuestionIfAnsweredAsync(request.SessionId, ct);
        return Ok(grant);
    }

    // The secret was never granted, so no session's copy holds it. The delete's strict sync can
    // still report some other session's copy as unwritable (#303); the value is out of the store
    // by then, which is what this is for.
    private async Task RemoveUngrantedAsync(string secretId)
    {
        try { await sessions.DeleteSessionSecretAsync(Owner, secretId, CancellationToken.None); }
        catch (SessionSecretCopiesPendingException) { }
    }

    [HttpPost("{id}/refuse")]
    public async Task<IActionResult> Refuse(string id, CancellationToken ct)
    {
        if (await requests.GetAsync(id, ct) is not { } request || request.Owner != Owner) return NotFound();
        if (await requests.ResolveAsync(id, SecretRequest.Refused, null, ct))
            await ClearQuestionIfAnsweredAsync(request.SessionId, ct);
        return NoContent();
    }

    // The session was flagged as waiting when it asked; once nothing it asked for is open, the
    // flag is the agent's own business again.
    private async Task ClearQuestionIfAnsweredAsync(string sessionId, CancellationToken ct)
    {
        if ((await requests.ListPendingAsync(Owner, sessionId, ct)).Count == 0)
            await sessions.ClearQuestionAsync(Owner, sessionId, ct);
    }
}
