// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Library sharing (MCP catalog & skills).
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Library;
using AgentHub.Api.Licensing;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Ee.Library;

/// <summary>
/// Enterprise sharing of library items with users, IdP groups, or everyone.
/// Personal owners manage their own entries; admins manage org (<c>__org__</c>)
/// MCP entries. Optionally, regular users may publish their own skills to
/// everyone (admin-controlled toggle).
/// </summary>
[ApiController]
[Authorize]
[Route("api/ee/library")]
public sealed class LibraryController(
    ILibraryShareStore store,
    IMcpServerStore mcpServers,
    ISkillStore skills,
    UserDirectory directory,
    AdminAccess admins,
    IEnterpriseLicense license) : ControllerBase
{
    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    private Task<bool> IsAdminAsync(CancellationToken ct) => admins.IsAdminAsync(Owner, ct);

    // ---------------------------------------------------------------- Users

    /// <summary>Known users for the sharing pickers (admin only).</summary>
    [HttpGet("users")]
    public async Task<IActionResult> ListUsers(CancellationToken ct)
    {
        if (await GateAsync(adminOnly: true, ct) is { } failure) return failure;
        var users = await directory.ListAsync(ct);
        return Ok(users
            .Select(u => new { owner = u.Owner, displayName = u.DisplayName, email = u.Email })
            .OrderBy(u => u.owner, StringComparer.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- Settings

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        if (await GateAsync(adminOnly: false, ct) is { } failure) return failure;
        return Ok(new LibrarySettings(await store.GetUserSkillPublishingAsync(ct)));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SetSettings(
        [FromBody] UpdateLibrarySettingsRequest request, CancellationToken ct)
    {
        if (await GateAsync(adminOnly: true, ct) is { } failure) return failure;
        await store.SetUserSkillPublishingAsync(request.UserSkillPublishing, ct);
        return Ok(new LibrarySettings(request.UserSkillPublishing));
    }

    // ---------------------------------------------------------------- Shares

    [HttpGet("{itemType}/{id}/shares")]
    public async Task<IActionResult> GetShares(string itemType, string id, CancellationToken ct)
    {
        if (await GateAsync(adminOnly: false, ct) is { } failure) return failure;
        if (MapItemType(itemType) is not { } type) return NotFound();
        if (!await CanManageSharesAsync(type, id, ct)) return NotFound();
        return Ok(await store.GetSharesAsync(type, id, ct));
    }

    [HttpPut("{itemType}/{id}/shares")]
    public async Task<IActionResult> SetShares(
        string itemType, string id, [FromBody] UpdateSharesRequest request, CancellationToken ct)
    {
        if (await GateAsync(adminOnly: false, ct) is { } failure) return failure;
        if (MapItemType(itemType) is not { } type) return NotFound();
        if (!await CanManageSharesAsync(type, id, ct)) return NotFound();

        if (!await IsAdminAsync(ct) && type == LibraryItemTypes.Skill)
        {
            // Regular users may only publish/unpublish their own skills to everyone,
            // and only while the admin toggle allows it. MCP catalog owners (and
            // admins for org entries) manage their shares without this restriction.
            var publishOnly = (request.Users is null or { Count: 0 })
                && (request.Groups is null or { Count: 0 });
            if (!publishOnly || !await store.GetUserSkillPublishingAsync(ct))
                return Forbid();
        }

        try
        {
            return Ok(await store.SetSharesAsync(
                type, id, request.All, request.Users, request.Groups, Owner, ct));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    // ---------------------------------------------------------------- Helpers

    private static string? MapItemType(string segment) => segment switch
    {
        "mcp-servers" => LibraryItemTypes.Mcp,
        "skills" => LibraryItemTypes.Skill,
        _ => null
    };

    private async Task<bool> CanManageSharesAsync(string type, string id, CancellationToken ct)
    {
        if (type == LibraryItemTypes.Mcp)
        {
            var record = (await mcpServers.GetManyAsync([id], ct)).FirstOrDefault();
            if (record is null) return false;
            if (record.Owner == Owner) return true;
            if (record.Owner == McpServerRecord.OrgOwner && await IsAdminAsync(ct))
                return true;
            return false;
        }

        return (await skills.GetManyAsync([id], ct)).Any(r => r.Owner == Owner);
    }

    private async Task<ObjectResult?> GateAsync(bool adminOnly, CancellationToken ct)
    {
        if (!license.Enabled)
        {
            return StatusCode(
                StatusCodes.Status402PaymentRequired,
                new { error = "An active enterprise license is required." });
        }
        if (adminOnly && !await IsAdminAsync(ct))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "Administrator access is required." });
        }
        return null;
    }
}
