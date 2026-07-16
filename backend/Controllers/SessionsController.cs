using System.Security.Claims;
using AgentHub.Api.Models;
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
    public SessionsController(ISessionService svc) => _svc = svc;

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

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
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (InvalidOperationException e) { return Conflict(e.Message); }
    }

    [HttpPatch("{id}")]
    public async Task<ActionResult<SessionInfo>> Update(string id, [FromBody] UpdateSessionRequest req, CancellationToken ct)
    {
        try { return Ok(await _svc.UpdateSessionAsync(Owner, id, req, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
    }

    [HttpPost("{id}/duplicate")]
    public async Task<ActionResult<SessionInfo>> Duplicate(string id, [FromBody] DuplicateSessionRequest request, CancellationToken ct)
    {
        try { return Ok(await _svc.DuplicateSessionAsync(Owner, id, request, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
    }

    [HttpPost("{id}/resume")]
    public async Task<ActionResult<SessionInfo>> Resume(string id, CancellationToken ct)
    {
        try { return Ok(await _svc.ResumeSessionAsync(Owner, id, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
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
}
