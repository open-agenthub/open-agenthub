using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[Route("api/git")]
public sealed class GitController : ControllerBase
{
    private readonly IGitAuthService _git;
    private readonly IConfiguration _cfg;
    public GitController(IGitAuthService git, IConfiguration cfg) { _git = git; _cfg = cfg; }

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "dev";

    // Callback lands on the public host (must match the OAuth app's redirect URI
    // exactly). Derived from FrontendOrigin rather than the request, because behind
    // a TLS-terminating ingress Request.Scheme is "http" and would not match the
    // registered https callback. Falls back to the request only if unset.
    private string RedirectUri(string providerId)
    {
        var origin = (_cfg["FrontendOrigin"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
        return $"{origin}/api/git/callback/{providerId}";
    }

    [Authorize]
    [HttpGet("providers")]
    public async Task<IReadOnlyList<GitProviderInfo>> Providers(CancellationToken ct)
        => await _git.ListProvidersAsync(Owner, ct);

    /// <summary>Returns the provider authorize URL for the browser to navigate to.</summary>
    [Authorize]
    [HttpGet("connect/{providerId}")]
    public ActionResult<object> Connect(string providerId)
    {
        try { return Ok(new { url = _git.CreateAuthorizeUrl(providerId, Owner, RedirectUri(providerId)) }); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
    }

    /// <summary>OAuth redirect target. Public (state is signed); redirects back to the account page.</summary>
    [AllowAnonymous]
    [HttpGet("callback/{providerId}")]
    public async Task<IActionResult> Callback(string providerId, [FromQuery] string? code, [FromQuery] string? state, CancellationToken ct)
    {
        var appUrl = (_cfg["FrontendOrigin"] ?? "/").TrimEnd('/');
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            return Redirect($"{appUrl}/account?git=error");
        var owner = await _git.HandleCallbackAsync(providerId, code, state, RedirectUri(providerId), ct);
        return Redirect($"{appUrl}/account?git={(owner is null ? "error" : "connected")}");
    }

    [Authorize]
    [HttpDelete("connections/{providerId}")]
    public async Task<IActionResult> Disconnect(string providerId, CancellationToken ct)
    {
        await _git.DisconnectAsync(Owner, providerId, ct);
        return NoContent();
    }

    [Authorize]
    [HttpGet("projects")]
    public async Task<ActionResult<IReadOnlyList<GitProject>>> Projects([FromQuery] string provider, [FromQuery] string? q, CancellationToken ct)
    {
        try { return Ok(await _git.SearchProjectsAsync(Owner, provider, q, ct)); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (InvalidOperationException e) { return BadRequest(e.Message); }
    }
}
