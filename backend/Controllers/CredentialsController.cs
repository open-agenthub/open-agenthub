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

    /// <summary>
    /// Forgets a stored provider login.
    ///
    /// These are not typed in here — a runtime captures them from the session the user signed in
    /// to and uploads them, so until now a login that stopped working was restored into every new
    /// session with nothing in the product able to clear it.
    ///
    /// Always 204, whether or not a login was stored: the endpoint is idempotent, and a 404 would
    /// let a caller enumerate which providers an account has signed in to.
    /// </summary>
    [HttpDelete("subscription/{agent}")]
    public async Task<IActionResult> DeleteSubscription(AgentKind agent, CancellationToken ct)
    {
        if (!Enum.IsDefined(agent)) return BadRequest("Unknown agent.");
        await _svc.DeleteProviderCredentialsAsync(Owner, agent, ct);
        return NoContent();
    }

    /// <summary>
    /// The caller's stored provider logins as accounts, keyed by agent name — labels, identity
    /// and timestamps, never the files (docs/provider-accounts.md).
    /// </summary>
    [HttpGet("accounts")]
    public async Task<ActionResult<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>>> Accounts(CancellationToken ct)
        => Ok(await _svc.ListProviderAccountsAsync(Owner, ct));

    /// <summary>Renames an account or makes it the default for new sessions.</summary>
    [HttpPatch("accounts/{agent}/{id}")]
    public async Task<ActionResult<ProviderAccountInfo>> UpdateAccount(AgentKind agent, string id,
        [FromBody] UpdateProviderAccountRequest req, CancellationToken ct)
    {
        if (!Enum.IsDefined(agent)) return BadRequest("Unknown agent.");
        if (!ProviderAccountSecret.IsValidId(id)) return BadRequest("Invalid account id.");
        try
        {
            var updated = await _svc.UpdateProviderAccountAsync(Owner, agent, id, req, ct);
            return updated is null ? NotFound() : Ok(updated);
        }
        catch (ArgumentException e) { return BadRequest(e.Message); }
    }

    /// <summary>Forgets one account. Always 204, for the same reason as <see cref="DeleteSubscription"/>.</summary>
    [HttpDelete("accounts/{agent}/{id}")]
    public async Task<IActionResult> DeleteAccount(AgentKind agent, string id, CancellationToken ct)
    {
        if (!Enum.IsDefined(agent)) return BadRequest("Unknown agent.");
        if (!ProviderAccountSecret.IsValidId(id)) return BadRequest("Invalid account id.");
        await _svc.DeleteProviderAccountAsync(Owner, agent, id, ct);
        return NoContent();
    }
}
