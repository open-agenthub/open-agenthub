using System.Security.Claims;
using AgentHub.Api.Agents;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/sessions")]
public sealed class SessionsController : ControllerBase
{
    private readonly ISessionService _svc;
    private readonly PermissionStore _permissions;
    private readonly IEnumerable<IPermissionPromptEditor> _promptEditors;

    public SessionsController(ISessionService svc, PermissionStore permissions,
        IEnumerable<IPermissionPromptEditor> promptEditors)
    { _svc = svc; _permissions = permissions; _promptEditors = promptEditors; }

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    private ObjectResult ForbiddenAgent(AgentNotAllowedException e)
        => StatusCode(StatusCodes.Status403Forbidden, e.Message);

    [HttpGet]
    public async Task<IReadOnlyList<SessionInfo>> List(CancellationToken ct)
        => await _svc.ListSessionsAsync(Owner, ct);

    [HttpGet("{id}")]
    public async Task<ActionResult<SessionInfo>> Get(string id, CancellationToken ct)
        => await _svc.GetSessionAsync(Owner, id, ct) is { } s ? Ok(s) : NotFound();

    [HttpPost]
    public async Task<ActionResult<SessionInfo>> Create([FromBody] CreateSessionRequest req, CancellationToken ct)
    {
        try { return Ok(await _svc.CreateSessionAsync(Owner, req, ct)); }
        catch (AgentNotAllowedException e) { return ForbiddenAgent(e); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (InvalidOperationException e) { return Conflict(e.Message); }
    }

    [HttpPatch("{id}")]
    public async Task<ActionResult<SessionInfo>> Update(string id, [FromBody] UpdateSessionRequest req, CancellationToken ct)
    {
        try { return Ok(await _svc.UpdateSessionAsync(Owner, id, req, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (AgentNotAllowedException e) { return ForbiddenAgent(e); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
    }

    [HttpPost("{id}/duplicate")]
    public async Task<ActionResult<SessionInfo>> Duplicate(string id, [FromBody] DuplicateSessionRequest request, CancellationToken ct)
    {
        try { return Ok(await _svc.DuplicateSessionAsync(Owner, id, request, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (AgentNotAllowedException e) { return ForbiddenAgent(e); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (InvalidOperationException e) { return Conflict(e.Message); }
    }

    [HttpPost("{id}/resume")]
    public async Task<ActionResult<SessionInfo>> Resume(string id, CancellationToken ct)
    {
        try { return Ok(await _svc.ResumeSessionAsync(Owner, id, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (AgentNotAllowedException e) { return ForbiddenAgent(e); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (InvalidOperationException e) { return Conflict(e.Message); }
    }

    [HttpPost("{id}/pause")]
    public async Task<ActionResult<SessionInfo>> Pause(string id, CancellationToken ct)
    {
        try { return Ok(await _svc.PauseSessionAsync(Owner, id, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
    }

    [HttpGet("{id}/transcript")]
    public async Task<IActionResult> Transcript(string id, CancellationToken ct)
    {
        var text = await _svc.GetTranscriptAsync(Owner, id, ct);
        return text is null ? NotFound() : Content(text, "text/plain");
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        try { await _svc.DeleteSessionAsync(Owner, id, ct); return NoContent(); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    public record PermissionDecisionBody(string Decision);

    /// <summary>Pending tool-permission requests of the session — polled by the web
    /// terminal so approvals also work in the app, not only via the messengers.</summary>
    [HttpGet("{id}/permissions")]
    public async Task<IActionResult> PendingPermissions(string id, CancellationToken ct)
    {
        if (await _svc.GetSessionAsync(Owner, id, ct) is null) return NotFound();
        var pending = await _permissions.GetPendingRequestsAsync(id, ct);
        return Ok(pending.Select(p => new { id = p.Id, tool = p.Tool, summary = p.Summary }));
    }

    /// <summary>Resolves a pending permission request from the web app. Mirrors the
    /// messenger buttons: allow | allowAlways | deny.</summary>
    [HttpPost("{id}/permissions/{reqId}")]
    public async Task<IActionResult> DecidePermission(string id, string reqId,
        [FromBody] PermissionDecisionBody body, CancellationToken ct)
    {
        if (await _svc.GetSessionAsync(Owner, id, ct) is null) return NotFound();
        if (body.Decision is not ("allow" or "allowAlways" or "deny")) return BadRequest("invalid decision");

        var resolved = await _permissions.ResolveAsync(reqId, body.Decision, id, ct);
        if (resolved is null)
        {
            var existing = await _permissions.GetAsync(reqId, id, ct);
            return existing is null
                ? NotFound()
                : Conflict(new { decision = existing.Decision });
        }
        // Defuse the messenger prompt (if one was posted) so it doesn't look alive.
        if (resolved.Platform is { } platform)
            foreach (var e in _promptEditors.Where(e => e.Platform == platform))
                await e.MarkDecidedAsync(resolved, body.Decision, ct);
        return Ok(new { decision = body.Decision });
    }
}
