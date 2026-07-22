// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Ee.Sharing;

[ApiController]
[AllowAnonymous]
[Route("api/shared/{token}")]
public sealed class SharedSessionsController(ISessionAccessService access) : ControllerBase
{
    [HttpGet("session")]
    public async Task<ActionResult<SharedSessionInfo>> GetSession(
        string token,
        CancellationToken ct)
    {
        var resolved = await access.ResolveTokenAsync(token, ct);
        return resolved is null
            ? NotFound()
            : Ok(SharedSessionSanitizer.Sanitize(resolved));
    }

    [HttpGet("transcript")]
    public async Task<IActionResult> GetTranscript(
        string token,
        [FromServices] ISessionService sessions,
        CancellationToken ct)
    {
        var resolved = await access.ResolveTokenAsync(token, ct);
        if (resolved is null)
            return NotFound();

        var transcript = await sessions.GetTranscriptAsync(
            resolved.Session.Owner,
            resolved.Session.Id,
            ct);
        return transcript is null
            ? NotFound()
            : Content(transcript, "text/plain");
    }
}
