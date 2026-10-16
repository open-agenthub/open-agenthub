// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Ee.Sharing;

/// <summary>
/// The web app's sharing surface. Every action delegates to <see cref="ISessionSharingService"/>,
/// which holds the licence check; this class only translates its exceptions into status codes.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ee/sessions/{sessionId}")]
public sealed class SharingController(ISessionSharingService sharing) : ControllerBase
{
    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    /// <summary>
    /// Sessions other owners have shared with the signed-in user, in the sanitised shape the
    /// shared-session page reads. An absolute template, because the controller prefix carries a
    /// session id and this listing has none; it stays here rather than in
    /// <c>SessionsController</c> because the data and the licence gate are Enterprise.
    /// </summary>
    [HttpGet("/api/sessions/shared")]
    public async Task<IActionResult> ListSharedWithMe(CancellationToken ct)
    {
        try
        {
            return Ok(await sharing.ListSharedWithAsync(Owner, ct));
        }
        catch (LicenseRequiredException exception)
        {
            return SharingResponses.LicenseRequired(this, exception);
        }
    }

    [HttpGet("shares")]
    public async Task<IActionResult> List(string sessionId, CancellationToken ct)
    {
        try
        {
            return Ok(await sharing.ListAsync(Owner, sessionId, ct));
        }
        catch (LicenseRequiredException exception)
        {
            return SharingResponses.LicenseRequired(this, exception);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("shares/users")]
    public async Task<IActionResult> CreateUser(
        string sessionId,
        [FromBody] CreateUserShareRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await sharing.ShareWithUserAsync(
                Owner,
                sessionId,
                request.Recipient,
                request.Role,
                ct));
        }
        catch (LicenseRequiredException exception)
        {
            return SharingResponses.LicenseRequired(this, exception);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return SharingResponses.BadRequest(this, exception);
        }
    }

    [HttpPatch("shares/users/{recipient}")]
    public async Task<IActionResult> UpdateUser(
        string sessionId,
        string recipient,
        [FromBody] UpdateShareRoleRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await sharing.ShareWithUserAsync(
                Owner,
                sessionId,
                recipient,
                request.Role,
                ct));
        }
        catch (LicenseRequiredException exception)
        {
            return SharingResponses.LicenseRequired(this, exception);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return SharingResponses.BadRequest(this, exception);
        }
    }

    [HttpDelete("shares/users/{recipient}")]
    public async Task<IActionResult> DeleteUser(
        string sessionId,
        string recipient,
        CancellationToken ct)
    {
        try
        {
            await sharing.UnshareUserAsync(Owner, sessionId, recipient, ct);
            return NoContent();
        }
        catch (LicenseRequiredException exception)
        {
            return SharingResponses.LicenseRequired(this, exception);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("shares/links")]
    public async Task<IActionResult> CreateLink(
        string sessionId,
        [FromBody] CreateShareLinkRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await sharing.CreateLinkAsync(
                Owner,
                sessionId,
                request.Role,
                request.ExpiresAt,
                ct));
        }
        catch (LicenseRequiredException exception)
        {
            return SharingResponses.LicenseRequired(this, exception);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return SharingResponses.BadRequest(this, exception);
        }
    }

    [HttpPatch("shares/links/{linkId}")]
    public async Task<IActionResult> UpdateLink(
        string sessionId,
        string linkId,
        [FromBody] UpdateShareLinkRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await sharing.UpdateLinkAsync(
                Owner,
                sessionId,
                linkId,
                request.Role,
                request.ExpiresAt,
                ct));
        }
        catch (LicenseRequiredException exception)
        {
            return SharingResponses.LicenseRequired(this, exception);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return SharingResponses.BadRequest(this, exception);
        }
    }

    [HttpDelete("shares/links/{linkId}")]
    public async Task<IActionResult> DeleteLink(
        string sessionId,
        string linkId,
        CancellationToken ct)
    {
        try
        {
            await sharing.DeleteLinkAsync(Owner, sessionId, linkId, ct);
            return NoContent();
        }
        catch (LicenseRequiredException exception)
        {
            return SharingResponses.LicenseRequired(this, exception);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("mcp-policy")]
    public async Task<IActionResult> SetMcpPolicy(
        string sessionId,
        [FromBody] UpdateMcpPolicyRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await sharing.SetMcpPolicyAsync(
                Owner,
                sessionId,
                request.BlockedServers,
                request.BlockedTools,
                ct));
        }
        catch (LicenseRequiredException exception)
        {
            return SharingResponses.LicenseRequired(this, exception);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return SharingResponses.BadRequest(this, exception);
        }
    }
}

/// <summary>The status codes both sharing controllers answer with, so the token surface and the
/// web surface cannot drift apart on what a licence failure or a bad recipient looks like.</summary>
public static class SharingResponses
{
    public static ObjectResult LicenseRequired(ControllerBase controller, LicenseRequiredException exception)
        => controller.StatusCode(StatusCodes.Status402PaymentRequired, new { error = exception.Message });

    /// <summary>400 with the message; an unknown recipient also carries <c>code</c>, because that
    /// is the one bad request a client can act on (wait for the person to sign in).</summary>
    public static BadRequestObjectResult BadRequest(ControllerBase controller, ArgumentException exception)
        => exception is UnknownRecipientException
            ? controller.BadRequest(new { error = exception.Message, code = "unknown_recipient" })
            : controller.BadRequest(new { error = exception.Message });
}
