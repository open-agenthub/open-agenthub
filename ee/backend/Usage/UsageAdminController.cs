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

    public UsageAdminController(IEnterpriseLicense license, AdminAccess access,
        UsageLimitStore limits, UserGroupStore groups)
    {
        _license = license;
        _access = access;
        _limits = limits;
        _groups = groups;
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
        try { await _groups.SetGroupRoleAsync(group.Trim(), req.Role, ct); }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
        return Ok(await _groups.ListGroupsAsync(ct));
    }
}
