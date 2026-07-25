// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Library sharing (MCP servers & skills).
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
/// Enterprise sharing of library items: admins manage user groups and share
/// MCP servers / skills with users, groups or everyone. Optionally, regular
/// users may publish their own skills to everyone (admin-controlled toggle).
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

    private bool IsAdmin => admins.IsAdmin(Owner);

    // ---------------------------------------------------------------- Groups

    [HttpGet("groups")]
    public async Task<IActionResult> ListGroups(CancellationToken ct)
    {
        if (Gate(adminOnly: true) is { } failure) return failure;
        return Ok(await store.ListGroupsAsync(ct));
    }

    [HttpPost("groups")]
    public async Task<IActionResult> CreateGroup([FromBody] CreateGroupRequest request, CancellationToken ct)
    {
        if (Gate(adminOnly: true) is { } failure) return failure;
        try
        {
            return Ok(await store.CreateGroupAsync(request.Name, ct));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpDelete("groups/{id}")]
    public async Task<IActionResult> DeleteGroup(string id, CancellationToken ct)
    {
        if (Gate(adminOnly: true) is { } failure) return failure;
        try
        {
            await store.DeleteGroupAsync(id, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("groups/{id}/members")]
    public async Task<IActionResult> SetGroupMembers(
        string id, [FromBody] SetGroupMembersRequest request, CancellationToken ct)
    {
        if (Gate(adminOnly: true) is { } failure) return failure;
        try
        {
            return Ok(await store.SetGroupMembersAsync(id, request.Members ?? [], ct));
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

    // ---------------------------------------------------------------- Users

    /// <summary>Known users for the sharing/group pickers (admin only).</summary>
    [HttpGet("users")]
    public async Task<IActionResult> ListUsers(CancellationToken ct)
    {
        if (Gate(adminOnly: true) is { } failure) return failure;
        var users = await directory.ListAsync(ct);
        return Ok(users
            .Select(u => new { owner = u.Owner, displayName = u.DisplayName, email = u.Email })
            .OrderBy(u => u.owner, StringComparer.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- Settings

    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings(CancellationToken ct)
    {
        if (Gate(adminOnly: false) is { } failure) return failure;
        return Ok(new LibrarySettings(await store.GetUserSkillPublishingAsync(ct)));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SetSettings(
        [FromBody] UpdateLibrarySettingsRequest request, CancellationToken ct)
    {
        if (Gate(adminOnly: true) is { } failure) return failure;
        await store.SetUserSkillPublishingAsync(request.UserSkillPublishing, ct);
        return Ok(new LibrarySettings(request.UserSkillPublishing));
    }

    // ---------------------------------------------------------------- Shares

    [HttpGet("{itemType}/{id}/shares")]
    public async Task<IActionResult> GetShares(string itemType, string id, CancellationToken ct)
    {
        if (Gate(adminOnly: false) is { } failure) return failure;
        if (MapItemType(itemType) is not { } type) return NotFound();
        if (!await OwnsItemAsync(type, id, ct)) return NotFound();
        return Ok(await store.GetSharesAsync(type, id, ct));
    }

    [HttpPut("{itemType}/{id}/shares")]
    public async Task<IActionResult> SetShares(
        string itemType, string id, [FromBody] UpdateSharesRequest request, CancellationToken ct)
    {
        if (Gate(adminOnly: false) is { } failure) return failure;
        if (MapItemType(itemType) is not { } type) return NotFound();
        if (!await OwnsItemAsync(type, id, ct)) return NotFound();

        if (!IsAdmin)
        {
            // Regular users may only publish/unpublish their own skills to everyone,
            // and only while the admin toggle allows it.
            var publishOnly = type == LibraryItemTypes.Skill
                && (request.Users is null or { Count: 0 })
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

    private async Task<bool> OwnsItemAsync(string type, string id, CancellationToken ct)
    {
        var owner = Owner;
        return type == LibraryItemTypes.Mcp
            ? (await mcpServers.GetManyAsync([id], ct)).Any(r => r.Owner == owner)
            : (await skills.GetManyAsync([id], ct)).Any(r => r.Owner == owner);
    }

    private ObjectResult? Gate(bool adminOnly)
    {
        if (!license.Enabled)
        {
            return StatusCode(
                StatusCodes.Status402PaymentRequired,
                new { error = "An active enterprise license is required." });
        }
        if (adminOnly && !IsAdmin)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = "Administrator access is required." });
        }
        return null;
    }
}
