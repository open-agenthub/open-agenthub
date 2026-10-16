using System.Security.Claims;
using System.Security.Cryptography;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Management of personal API tokens. Tokens let a user drive their own sessions
/// remotely (see <see cref="RemoteController"/>). The plaintext token is returned
/// only once, at creation; afterwards only a non-secret prefix is shown.
/// </summary>
[ApiController]
[Authorize]
[Route("api/tokens")]
public sealed class ApiTokensController : ControllerBase
{
    private readonly ApiTokenStore _store;
    private readonly ISessionService _sessions;

    public ApiTokensController(ApiTokenStore store, ISessionService sessions)
    {
        _store = store;
        _sessions = sessions;
    }

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "dev";

    /// <param name="AllowedCredentials">Restricts sessions created with the token to these
    /// credentials (docs/credential-scopes.md); omitted = any.</param>
    public record CreateTokenRequest(string Name, ApiTokenScope? AllowedCredentials = null);
    public record CreatedToken(string Id, string Name, string Prefix, DateTime CreatedAt, string Token,
        ApiTokenScope? AllowedCredentials);
    /// <param name="AllowedCredentials">The new restriction; null lifts it.</param>
    public record UpdateTokenRequest(ApiTokenScope? AllowedCredentials);

    /// <summary>Lists the user's tokens. The token secret is never returned.</summary>
    [HttpGet]
    public async Task<IReadOnlyList<ApiTokenInfo>> List(CancellationToken ct)
        => await _store.ListByOwnerAsync(Owner, ct);

    /// <summary>Creates a token and returns the plaintext value exactly once.</summary>
    [HttpPost]
    public async Task<ActionResult<CreatedToken>> Create([FromBody] CreateTokenRequest req, CancellationToken ct)
    {
        var name = req?.Name?.Trim();
        if (string.IsNullOrEmpty(name)) return BadRequest("Name is required.");
        if (name.Length > 100) return BadRequest("Name is too long.");

        ApiTokenScope? scope;
        try { scope = await NormalizeScopeAsync(req!.AllowedCredentials, ct); }
        catch (ArgumentException e) { return BadRequest(e.Message); }

        var token = GenerateToken();
        var info = await _store.CreateAsync(Owner, name, token, scope, ct);
        return Ok(new CreatedToken(info.Id, info.Name, info.Prefix, info.CreatedAt, token, info.AllowedCredentials));
    }

    /// <summary>Replaces a token's credential restriction; <c>{"allowedCredentials": null}</c> lifts it.</summary>
    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateTokenRequest req, CancellationToken ct)
    {
        ApiTokenScope? scope;
        try { scope = await NormalizeScopeAsync(req?.AllowedCredentials, ct); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        return await _store.UpdateScopeAsync(Owner, id, scope, ct) ? NoContent() : NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
        => await _store.DeleteAsync(Owner, id, ct) ? NoContent() : NotFound();

    /// <summary>Checks every id in a scope against what the caller has stored right now, so a
    /// typo is a 400 here and not a token that silently allows nothing.</summary>
    private async Task<ApiTokenScope?> NormalizeScopeAsync(ApiTokenScope? scope, CancellationToken ct)
    {
        if (scope is null) return null;
        var accounts = await _sessions.ListProviderAccountsAsync(Owner, ct);
        var status = await _sessions.GetCredentialStatusAsync(Owner, ct);
        return ApiTokenScope.Normalize(scope, accounts, status.GitPats);
    }

    /// <summary>Format: "oah_" + 32 random bytes as lowercase hex.</summary>
    private static string GenerateToken()
        => "oah_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
