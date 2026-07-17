using System.Security.Claims;
using AgentHub.Api.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Ee.Sharing;

[ApiController]
[Authorize]
[Route("api/ee/sessions/{sessionId}")]
public sealed class SharingController(
    SessionShareStore store,
    IEnterpriseLicense license) : ControllerBase
{
    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    [HttpGet("shares")]
    public async Task<IActionResult> List(string sessionId, CancellationToken ct)
    {
        if (LicenseFailure() is { } failure)
            return failure;

        try
        {
            return Ok(await store.ListForOwnerAsync(Owner, sessionId, ct));
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
        if (LicenseFailure() is { } failure)
            return failure;

        try
        {
            return Ok(await store.UpsertDirectAsync(
                Owner,
                sessionId,
                request.Recipient,
                request.Role,
                ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPatch("shares/users/{recipient}")]
    public async Task<IActionResult> UpdateUser(
        string sessionId,
        string recipient,
        [FromBody] UpdateShareRoleRequest request,
        CancellationToken ct)
    {
        if (LicenseFailure() is { } failure)
            return failure;

        try
        {
            return Ok(await store.UpsertDirectAsync(
                Owner,
                sessionId,
                recipient,
                request.Role,
                ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpDelete("shares/users/{recipient}")]
    public async Task<IActionResult> DeleteUser(
        string sessionId,
        string recipient,
        CancellationToken ct)
    {
        if (LicenseFailure() is { } failure)
            return failure;

        try
        {
            await store.DeleteDirectAsync(Owner, sessionId, recipient, ct);
            return NoContent();
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
        if (LicenseFailure() is { } failure)
            return failure;

        try
        {
            var issued = await store.CreateLinkAsync(
                Owner,
                sessionId,
                request.Role,
                request.ExpiresAt,
                ct);
            return Ok(new CreatedShareLinkResponse(
                issued.Link,
                $"/shared/{Uri.EscapeDataString(issued.Token)}"));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPatch("shares/links/{linkId}")]
    public async Task<IActionResult> UpdateLink(
        string sessionId,
        string linkId,
        [FromBody] UpdateShareLinkRequest request,
        CancellationToken ct)
    {
        if (LicenseFailure() is { } failure)
            return failure;

        try
        {
            return Ok(await store.UpdateLinkAsync(
                Owner,
                sessionId,
                linkId,
                request.Role,
                request.ExpiresAt,
                ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpDelete("shares/links/{linkId}")]
    public async Task<IActionResult> DeleteLink(
        string sessionId,
        string linkId,
        CancellationToken ct)
    {
        if (LicenseFailure() is { } failure)
            return failure;

        try
        {
            await store.DeleteLinkAsync(Owner, sessionId, linkId, ct);
            return NoContent();
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
        if (LicenseFailure() is { } failure)
            return failure;

        try
        {
            var policy = await store.SetMcpPolicyAsync(
                Owner,
                sessionId,
                request.BlockedServers,
                request.BlockedTools,
                ct);
            return Ok(policy);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    private ObjectResult? LicenseFailure()
        => license.Enabled
            ? null
            : StatusCode(
                StatusCodes.Status402PaymentRequired,
                new { error = "An active enterprise license is required." });
}
