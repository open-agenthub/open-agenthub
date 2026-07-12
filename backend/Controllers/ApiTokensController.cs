using System.Security.Claims;
using System.Security.Cryptography;
using AgentHub.Api.Persistence;
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
    public ApiTokensController(ApiTokenStore store) => _store = store;

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "dev";

    public record CreateTokenRequest(string Name);
    public record CreatedToken(string Id, string Name, string Prefix, DateTime CreatedAt, string Token);

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

        var token = GenerateToken();
        var info = await _store.CreateAsync(Owner, name, token, ct);
        return Ok(new CreatedToken(info.Id, info.Name, info.Prefix, info.CreatedAt, token));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
        => await _store.DeleteAsync(Owner, id, ct) ? NoContent() : NotFound();

    /// <summary>Format: "oah_" + 32 random bytes as lowercase hex.</summary>
    private static string GenerateToken()
        => "oah_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
