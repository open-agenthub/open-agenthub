// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Admin API for usage limits & group roles.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Ee.Identity;
using AgentHub.Api.Licensing;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Ee.Usage;

/// <summary>
/// Enterprise admin API: monthly API budgets (global / per group / per user) and the
/// group→role mapping (groups are read from the OAuth token at login). Every action
/// requires a valid enterprise license (402 otherwise) and an admin caller (403).
/// </summary>
[ApiController]
[Authorize]
[Route("api/ee/admin")]
public sealed class UsageAdminController : ControllerBase
{
    private readonly IEnterpriseLicense _license;
    private readonly AdminAccess _access;
    private readonly UsageLimitStore _limits;
    private readonly UserGroupStore _groups;
    private readonly IUserRoleStore _roles;
    private readonly UserDirectory _dir;

    public UsageAdminController(IEnterpriseLicense license, AdminAccess access,
        UsageLimitStore limits, UserGroupStore groups, IUserRoleStore roles, UserDirectory dir)
    {
        _license = license;
        _access = access;
        _limits = limits;
        _groups = groups;
        _roles = roles;
        _dir = dir;
    }

    private string Owner =>
        User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "dev";

    private ObjectResult? LicenseFailure()
        => _license.Enabled
            ? null
            : StatusCode(StatusCodes.Status402PaymentRequired,
                new { error = "An active enterprise license is required." });

    private async Task<IActionResult?> GateAsync(CancellationToken ct)
    {
        if (LicenseFailure() is { } payment) return payment;
        if (!await _access.IsAdminAsync(Owner, ct)) return Forbid();
        return null;
    }

    // ---------------------------------------------------------------- Limits

    [HttpGet("limits")]
    public async Task<IActionResult> ListLimits(CancellationToken ct)
    {
        if (await GateAsync(ct) is { } fail) return fail;
        return Ok(await _limits.ListAsync(ct));
    }

    public sealed record SetLimitReq(string Scope, string? Target, double LimitUsd);

    [HttpPut("limits")]
    public async Task<IActionResult> SetLimit([FromBody] SetLimitReq req, CancellationToken ct)
    {
        if (await GateAsync(ct) is { } fail) return fail;
        try { await _limits.SetAsync(req.Scope, req.Target ?? "", req.LimitUsd, ct); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
        return Ok(await _limits.ListAsync(ct));
    }

    [HttpDelete("limits/{scope}")]
    public async Task<IActionResult> DeleteLimit(string scope, [FromQuery] string? target, CancellationToken ct)
    {
        if (await GateAsync(ct) is { } fail) return fail;
        try
        {
            return await _limits.DeleteAsync(scope, target ?? "", ct) ? NoContent() : NotFound();
        }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
    }

    // ---------------------------------------------------------------- Groups & roles

    /// <summary>All known groups (from token claims and role mappings) with member counts.</summary>
    [HttpGet("groups")]
    public async Task<IActionResult> ListGroups(CancellationToken ct)
    {
        if (await GateAsync(ct) is { } fail) return fail;
        return Ok(await _groups.ListGroupsAsync(ct));
    }

    public sealed record SetRoleReq(string? Role);

    /// <summary>Maps a group to a role ("admin" | "user"); null clears the mapping.</summary>
    [HttpPut("groups/{group}/role")]
    public async Task<IActionResult> SetGroupRole(string group, [FromBody] SetRoleReq req, CancellationToken ct)
    {
        if (await GateAsync(ct) is { } fail) return fail;
        if (string.IsNullOrWhiteSpace(group)) return BadRequest(new { error = "Group name is required." });
        if (req.Role is not (null or UserGroupStore.AdminRole or UserGroupStore.UserRole))
            return BadRequest(new { error = $"Unknown role '{req.Role}'." });
        // Routing decodes everything in a path segment except %2F, which it leaves encoded so it
        // cannot split the segment. Keycloak subgroup paths ("team/sub") contain slashes, and
        // left as "team%2Fsub" the mapping would never match a member.
        var name = GroupClaims.Normalize(Uri.UnescapeDataString(group));
        if (name.Length == 0) return BadRequest(new { error = "Group name is required." });

        await using (await _roles.LockAsync(ct))
        {
            var bootstrapBefore = await _access.IsBootstrapActiveAsync(ct);
            var previous = await _groups.GetGroupRoleAsync(name, ct);
            await _groups.SetGroupRoleAsync(name, req.Role, ct);
            // Unmapping or downgrading the only admin group can leave nobody able to reach the
            // admin area; check the outcome with the real precedence rules and undo it if so.
            var known = await _dir.ListAsync(ct);
            if (!await _access.KeepsAnAdminAsync(bootstrapBefore, known.Select(u => u.Owner), ct))
            {
                await _groups.SetGroupRoleAsync(name, previous, ct);
                return Conflict(new { error = "This would leave no admin. Make someone else an admin first." });
            }
            if (previous != req.Role)
                await _roles.AuditAsync(Owner, "group", name, previous, req.Role, ct);
        }
        return Ok(await _groups.ListGroupsAsync(ct));
    }
}
