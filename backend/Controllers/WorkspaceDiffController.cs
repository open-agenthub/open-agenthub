using System.Security.Claims;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/sessions/{id}/diff")]
public sealed class WorkspaceDiffController(ISessionAccessService access, IWorkspaceDiffReader reader) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Read(string id, CancellationToken ct, [FromQuery] string comparison = "base")
    {
        Response.Headers.CacheControl = "no-store";
        if (comparison is not ("base" or "head")) return BadRequest();
        var principal = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal is null) return Unauthorized();
        var resolved = await access.ResolveUserAsync(principal, id, ct);
        if (resolved is null || !SessionAccessRules.CanReadFiles(resolved.Level)) return NotFound();
        var json = await reader.ReadAsync(resolved.Session, comparison, ct);
        return json is null ? StatusCode(503, new { error = "diff_unavailable" }) : Content(json, "application/json");
    }
}
