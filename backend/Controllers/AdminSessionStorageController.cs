using System.Security.Claims;
using System.Security.Cryptography;
using AgentHub.Api.Admin;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/admin/session-storage-cleanup")]
public sealed class AdminSessionStorageController(AdminAccess access, SessionStorageCleanup cleanup) : ControllerBase
{
    private string Owner => User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "dev";
    public sealed record ApplyRequest(string Token);

    [HttpPost("dry-run")]
    public async Task<IActionResult> Preview(CancellationToken ct)
    {
        if (!await access.IsAdminAsync(Owner, ct)) return Forbid();
        return Ok(await cleanup.PreviewAsync(Owner, ct));
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply(ApplyRequest request, CancellationToken ct)
    {
        if (!await access.IsAdminAsync(Owner, ct)) return Forbid();
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 256_000)
            return BadRequest(new { error = "A dry-run token is required." });
        try { return Ok(await cleanup.ApplyAsync(Owner, request.Token, ct)); }
        catch (CryptographicException) { return BadRequest(new { error = "Invalid dry-run token; run a new dry run." }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
