using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/credentials")]
public sealed class CredentialsController : ControllerBase
{
    private readonly ISessionService _svc;
    public CredentialsController(ISessionService svc) => _svc = svc;

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// Credentials are written directly to a per-user K8s secret and
    /// deliberately never read back here (write-only).
    /// </summary>
    [HttpPut]
    public async Task<IActionResult> Store([FromBody] UserCredentials creds, CancellationToken ct)
    {
        await _svc.StoreCredentialsAsync(Owner, creds, ct);
        return NoContent();
    }

    /// <summary>Which fields currently have a stored value — never the values themselves.</summary>
    [HttpGet]
    public async Task<ActionResult<CredentialStatus>> Status(CancellationToken ct)
        => Ok(await _svc.GetCredentialStatusAsync(Owner, ct));
}
