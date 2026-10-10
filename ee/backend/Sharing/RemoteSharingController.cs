// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Controllers;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHub.Api.Ee.Sharing;

/// <summary>
/// Session sharing for a caller holding a personal API token — what the "Share" dialog does,
/// reachable from a script or an agent. Lives beside <see cref="RemoteController"/> on the
/// <c>api/remote</c> surface and authenticates the same way (the token in the Authorization
/// header, resolved by <see cref="ApiTokenStore"/>), but in its own class because the data and
/// the licence gate are Enterprise. Status codes match the web surface: 402 without a licence,
/// 404 for a session that is not the token owner's, 400 for a recipient who has never signed in.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/remote/sessions")]
public sealed class RemoteSharingController : ControllerBase
{
    private readonly Func<string, CancellationToken, Task<string?>> _findOwner;
    private readonly ISessionSharingService _sharing;

    /// <remarks>Marked for the container for the reason given on <see cref="RemoteController"/>:
    /// with two public constructors the activator cannot choose, and every route here would
    /// answer 500 before any action ran.</remarks>
    [ActivatorUtilitiesConstructor]
    public RemoteSharingController(ApiTokenStore tokens, ISessionSharingService sharing)
        : this(tokens.FindOwnerByTokenAsync, sharing) { }

    /// <summary>Test seam: resolve the owner from a plaintext token without Postgres.</summary>
    public RemoteSharingController(
        Func<string, CancellationToken, Task<string?>> findOwnerByToken,
        ISessionSharingService sharing)
    {
        _findOwner = findOwnerByToken;
        _sharing = sharing;
    }

    private async Task<string?> ResolveOwnerAsync(CancellationToken ct)
        => RemoteBearerToken.Read(Request) is { } token ? await _findOwner(token, ct) : null;

    /// <summary>Sessions other owners have shared with the token owner, sanitised as on the
    /// shared-session page. A path of its own rather than a flag on the session listing, so each
    /// route keeps one response shape.</summary>
    [HttpGet("shared")]
    public async Task<IActionResult> ListSharedWithMe(CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try { return Ok(await _sharing.ListSharedWithAsync(owner, ct)); }
        catch (LicenseRequiredException e) { return SharingResponses.LicenseRequired(this, e); }
    }

    [HttpGet("{id}/shares")]
    public async Task<IActionResult> List(string id, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try { return Ok(await _sharing.ListAsync(owner, id, ct)); }
        catch (LicenseRequiredException e) { return SharingResponses.LicenseRequired(this, e); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("{id}/shares/users")]
    public async Task<IActionResult> ShareWithUser(
        string id, [FromBody] CreateUserShareRequest request, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try { return Ok(await _sharing.ShareWithUserAsync(owner, id, request.Recipient, request.Role, ct)); }
        catch (LicenseRequiredException e) { return SharingResponses.LicenseRequired(this, e); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return SharingResponses.BadRequest(this, e); }
    }

    [HttpDelete("{id}/shares/users/{recipient}")]
    public async Task<IActionResult> UnshareUser(string id, string recipient, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try
        {
            await _sharing.UnshareUserAsync(owner, id, recipient, ct);
            return NoContent();
        }
        catch (LicenseRequiredException e) { return SharingResponses.LicenseRequired(this, e); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    /// <summary>Mints a secret link. <c>url</c> is absolute (<c>FrontendOrigin</c> +
    /// <c>/shared/{token}</c>) because the caller is not in a browser and has nothing to resolve a
    /// path against; it is the only time the token is shown.</summary>
    [HttpPost("{id}/shares/links")]
    public async Task<IActionResult> CreateLink(
        string id, [FromBody] CreateShareLinkRequest request, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try { return Ok(await _sharing.CreateLinkAsync(owner, id, request.Role, request.ExpiresAt, ct)); }
        catch (LicenseRequiredException e) { return SharingResponses.LicenseRequired(this, e); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException e) { return SharingResponses.BadRequest(this, e); }
    }

    [HttpDelete("{id}/shares/links/{linkId}")]
    public async Task<IActionResult> DeleteLink(string id, string linkId, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try
        {
            await _sharing.DeleteLinkAsync(owner, id, linkId, ct);
            return NoContent();
        }
        catch (LicenseRequiredException e) { return SharingResponses.LicenseRequired(this, e); }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
