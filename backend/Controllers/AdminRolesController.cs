using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Licensing;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Admin area, Community Edition: who is an admin and why, and making a known user an admin or
/// a regular user without a values edit. Group→role mappings are the Enterprise counterpart
/// (<c>/api/ee/admin/groups</c>); both feed <see cref="AdminAccess"/>. See docs/admin-roles.md.
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin")]
public sealed class AdminRolesController : ControllerBase
{
    private readonly AdminAccess _access;
    private readonly IUserRoleStore _roles;
    private readonly UserDirectory _dir;
    private readonly IEnterpriseLicense _license;

    public AdminRolesController(AdminAccess access, IUserRoleStore roles, UserDirectory dir,
        IEnterpriseLicense license)
    { _access = access; _roles = roles; _dir = dir; _license = license; }

    private string Owner =>
        User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "dev";

    public sealed record UserRoleRow(
        string Owner, string? Email, string? DisplayName,
        string Role, string Source, IReadOnlyList<string> Groups, string? ExplicitRole);

    public sealed record RolesOverview(
        bool Bootstrap, bool GroupMapping, IReadOnlyCollection<string> ValuesAdmins,
        IReadOnlyList<UserRoleRow> Users);

    [HttpGet("roles")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (!await _access.IsAdminAsync(Owner, ct)) return Forbid();
        return Ok(await OverviewAsync(ct));
    }

    public sealed record SetUserRoleReq(string? Role);

    /// <summary>Sets a known user's role ("admin" | "user"); null clears it so groups decide again.</summary>
    [HttpPut("users/{owner}/role")]
    public async Task<IActionResult> SetRole(string owner, [FromBody] SetUserRoleReq req, CancellationToken ct)
    {
        if (!await _access.IsAdminAsync(Owner, ct)) return Forbid();
        if (req.Role is not (null or UserRoleStore.AdminRole or UserRoleStore.UserRole))
            return BadRequest(new { error = $"Unknown role '{req.Role}'." });
        if (_access.IsValuesAdmin(owner))
            return Conflict(new { error = $"{owner} is listed in Ee:Admins (Helm values) and stays an admin; remove them there to change this." });

        var known = await _dir.ListAsync(ct);
        var target = known.FirstOrDefault(u => string.Equals(u.Owner, owner, StringComparison.OrdinalIgnoreCase));
        if (target is null) return NotFound(new { error = $"No user {owner} has signed in yet." });

        await using (await _roles.LockAsync(ct))
        {
            var bootstrapBefore = await _access.IsBootstrapActiveAsync(ct);
            var previous = await _roles.SetRoleAsync(target.Owner, req.Role, ct);
            // The precedence rules live in AdminAccess alone; checking the outcome rather than
            // predicting it keeps them from being restated (and drifting) here.
            if (!await _access.KeepsAnAdminAsync(bootstrapBefore, known.Select(u => u.Owner), ct))
            {
                await _roles.SetRoleAsync(target.Owner, previous, ct);
                return Conflict(new { error = "This would leave no admin. Make someone else an admin first." });
            }
            if (previous != req.Role)
                await _roles.AuditAsync(Owner, "user", target.Owner, previous, req.Role, ct);
        }
        return Ok(await OverviewAsync(ct));
    }

    [HttpGet("roles/audit")]
    public async Task<IActionResult> Audit([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (!await _access.IsAdminAsync(Owner, ct)) return Forbid();
        return Ok(await _roles.ListAuditAsync(limit, ct));
    }

    private async Task<RolesOverview> OverviewAsync(CancellationToken ct)
    {
        var users = await _dir.ListAsync(ct);
        var explicitRoles = await _roles.ListRolesAsync(ct);
        var bootstrap = await _access.IsBootstrapActiveAsync(ct);
        var rows = new List<UserRoleRow>(users.Count);
        foreach (var u in users)
        {
            explicitRoles.TryGetValue(u.Owner, out var explicitRole);
            var resolved = await _access.ResolveAsync(u.Owner, explicitRole, bootstrap, ct);
            rows.Add(new UserRoleRow(u.Owner, u.Email, u.DisplayName,
                resolved.Role, resolved.Source, resolved.Groups, explicitRole));
        }
        return new RolesOverview(bootstrap, _license.Enabled, _access.ValuesAdmins, rows);
    }
}
