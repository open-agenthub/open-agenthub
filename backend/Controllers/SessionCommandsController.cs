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
    // The same access as Files and the diff: the list names command and skill files on the pod,
    // which anyone who may read the session's files can already see.
    [HttpGet]
    public async Task<IActionResult> Read(string id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var principal = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal is null) return Unauthorized();
        var resolved = await access.ResolveUserAsync(principal, id, ct);
        if (resolved is null || !SessionAccessRules.CanReadFiles(resolved.Level)) return NotFound();
        var json = await reader.ReadAsync(resolved.Session, ct);
        return json is null ? StatusCode(503, new { error = "commands_unavailable" }) : Content(json, "application/json");
    }
}
