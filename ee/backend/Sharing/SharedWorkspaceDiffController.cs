// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — shared workspace diff reads.
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
[Route("api/shared/{token}/diff")]
public sealed class SharedWorkspaceDiffController(ISessionAccessService access, IWorkspaceDiffReader reader) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Read(string token, CancellationToken ct, [FromQuery] string comparison = "base")
    {
        Response.Headers.CacheControl = "no-store";
        if (comparison is not ("base" or "head")) return BadRequest();
        var resolved = await access.ResolveTokenReadOnlyAsync(token, ct);
        if (resolved is null || !SessionAccessRules.CanReadFiles(resolved.Level)) return NotFound();
        var json = await reader.ReadAsync(resolved.Session, comparison, ct);
        return json is null ? StatusCode(503, new { error = "diff_unavailable" }) : Content(json, "application/json");
    }
}
