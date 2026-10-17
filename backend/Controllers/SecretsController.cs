using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// The owner's named secrets for sessions (docs/session-secrets.md). Write-only like the other
/// credentials: every answer carries id, name, delivery and description, never a value.
/// </summary>
[ApiController]
[Authorize]
[Route("api/secrets")]
public sealed class SecretsController : ControllerBase
{
    private readonly ISessionService _svc;
    public SecretsController(ISessionService svc) => _svc = svc;

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SessionSecretInfo>>> List(CancellationToken ct)
        => Ok(await _svc.ListSessionSecretsAsync(Owner, ct));

    /// <summary>Stores a secret, or updates the one with the same name. A running session that
    /// receives it gets a changed file within the kubelet's sync period, a changed variable at
    /// its next start.</summary>
    [HttpPost]
    public async Task<ActionResult<SessionSecretInfo>> Upsert([FromBody] UpsertSessionSecretRequest request, CancellationToken ct)
    {
        try { return Ok(await _svc.UpsertSessionSecretAsync(Owner, request, ct)); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
    }

    /// <summary>Always 204, so the status code cannot be used to probe which ids exist.</summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        await _svc.DeleteSessionSecretAsync(Owner, id, ct);
        return NoContent();
    }
}
