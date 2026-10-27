using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/admin/session-storage-cleanup/claude-backups")]
public sealed class AdminClaudeStateCleanupController(AdminAccess access, ClaudeStateCleanup cleanup) : ControllerBase
{
    public sealed record CleanupRequest(string Owner, bool Apply = false);

    [HttpPost]
    public async Task<IActionResult> Run(CleanupRequest request, CancellationToken ct)
    {
        var admin = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "dev";
        if (!await access.IsAdminAsync(admin, ct)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Owner) || request.Owner.Length > 512)
            return BadRequest(new { error = "An owner is required." });
        return Ok(await cleanup.RunAsync(request.Owner, request.Apply, ct));
    }
}
