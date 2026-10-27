using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// The owner's side of a secret request (docs/session-secrets.md): what the agents asked for, and
/// the answer — a stored secret granted to that one session, or a refusal. The value itself is
/// stored through <see cref="SecretsController"/> first; nothing here carries one.
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
    /// Grants a stored secret to the requesting session and closes the request, also for a session
    /// created by a restricted token or by another session (#330); 409 when the request was closed
    /// in the meantime.
    /// </summary>
    [HttpPost("{id}/provide")]
    public async Task<ActionResult<SessionSecretGrant>> Provide(string id, [FromBody] ProvideSecretRequestBody body, CancellationToken ct)
    {
        if (await requests.GetAsync(id, ct) is not { } request || request.Owner != Owner) return NotFound();
        if (request.Status != SecretRequest.Pending) return Conflict(new { error = "request_closed", status = request.Status });
        if (string.IsNullOrWhiteSpace(body.SecretId)) return BadRequest("secretId is required.");
        SessionSecretGrant grant;
        try { grant = await sessions.GrantSessionSecretAsync(Owner, request.SessionId, body.SecretId.Trim(), ct); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        // The grant stands even when the tool gave up a moment ago: the owner meant to give the
        // session this secret, and the next request for it is then answered at once.
        if (!await requests.ResolveAsync(id, SecretRequest.Provided, grant.SecretId, ct))
            return Conflict(new { error = "request_closed" });
        await ClearQuestionIfAnsweredAsync(request.SessionId, ct);
        return Ok(grant);
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
