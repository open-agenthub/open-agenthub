using System.Security.Claims;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/sessions/{id}/commands")]
public sealed class SessionCommandsController(ISessionAccessService access, ISessionCommandsReader reader) : ControllerBase
{
    // Only someone who can type into the agent gets its command list: a viewer has no composer,
    // and the list names the custom commands and skills on the pod.
    [HttpGet]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var principal = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal is null) return Unauthorized();
        var resolved = await access.ResolveUserAsync(principal, id, ct);
        if (resolved is null || !SessionAccessRules.CanWriteTerminal(resolved.Level)) return NotFound();
        var json = await reader.ReadAsync(resolved.Session, ct);
        return json is null ? StatusCode(503, new { error = "commands_unavailable" }) : Content(json, "application/json");
    }
}
