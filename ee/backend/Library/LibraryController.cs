// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Library sharing (MCP catalog).
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Library;
using AgentHub.Api.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Ee.Library;

/// <summary>
/// Enterprise sharing of MCP catalog entries with users, IdP groups, or everyone.
/// Personal owners manage their own entries; admins manage org (<c>__org__</c>) entries.
/// </summary>
[ApiController]
[Authorize]
[Route("api/ee/library/mcp-servers")]
public sealed class LibraryController(
    ILibraryShareStore store,
    IMcpServerStore mcpServers,
    AdminAccess admins,
    IEnterpriseLicense license) : ControllerBase
{
    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    [HttpGet("{id}/shares")]
    public async Task<IActionResult> GetShares(string id, CancellationToken ct)
    {
        if (LicenseFailure() is { } failure) return failure;
        if (!await CanManageSharesAsync(id, ct)) return NotFound();
        return Ok(await store.GetSharesAsync(LibraryItemTypes.Mcp, id, ct));
    }

    [HttpPut("{id}/shares")]
    public async Task<IActionResult> SetShares(
        string id, [FromBody] UpdateSharesRequest request, CancellationToken ct)
    {
        if (LicenseFailure() is { } failure) return failure;
        if (!await CanManageSharesAsync(id, ct)) return NotFound();

        try
        {
            return Ok(await store.SetSharesAsync(
                LibraryItemTypes.Mcp, id, request.All, request.Users, request.Groups, Owner, ct));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    private async Task<bool> CanManageSharesAsync(string id, CancellationToken ct)
    {
        var record = (await mcpServers.GetManyAsync([id], ct)).FirstOrDefault();
        if (record is null) return false;
        if (record.Owner == Owner) return true;
        if (record.Owner == McpServerRecord.OrgOwner && await admins.IsAdminAsync(Owner, ct))
            return true;
        return false;
    }

    private ObjectResult? LicenseFailure()
        => license.Enabled
            ? null
            : StatusCode(
                StatusCodes.Status402PaymentRequired,
                new { error = "An active enterprise license is required." });
}
