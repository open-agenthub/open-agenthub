using System.Security.Claims;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/account")]
public sealed class AccountController : ControllerBase
{
    private readonly AccountDeletionService _deletion;
    private readonly ILogger<AccountController> _log;

    public AccountController(AccountDeletionService deletion, ILogger<AccountController> log)
    {
        _deletion = deletion;
        _log = log;
    }

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// Deletes the caller's OWN account and all its data (GDPR). Irreversible.
    /// The caller must confirm by passing their own username — a stray DELETE
    /// without it does nothing.
    /// </summary>
    [HttpDelete]
    public async Task<IActionResult> Delete([FromQuery] string? confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, Owner, StringComparison.Ordinal))
            return BadRequest("Pass your username as ?confirm= to delete the account.");
        _log.LogInformation("Account deletion requested by its owner");
        await _deletion.DeleteAccountAsync(Owner, ct);
        return NoContent();
    }
}
