// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Usage-limit resolution & admin role source.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Admin;
using AgentHub.Api.Ee.Identity;
using AgentHub.Api.Licensing;
using AgentHub.Api.Usage;

namespace AgentHub.Api.Ee.Usage;

/// <summary>
/// Feeds the admin-configured limits (global / group / user) into the community
/// <see cref="UsageLimitService"/>. Gated at runtime: without a valid enterprise
/// license no admin limit applies (the personal limit still does).
/// Resolution: the strictest (lowest) limit among global, the user's groups, and
/// the user-specific row wins.
/// </summary>
public sealed class EeUsageLimitProvider : IAdminUsageLimitProvider
{
    private readonly IEnterpriseLicense _license;
    private readonly UsageLimitStore _limits;
    private readonly UserGroupStore _groups;

    public EeUsageLimitProvider(IEnterpriseLicense license, UsageLimitStore limits, UserGroupStore groups)
    {
        _license = license;
        _limits = limits;
        _groups = groups;
    }

    public async Task<UsageLimit?> GetLimitAsync(string owner, CancellationToken ct = default)
    {
        if (!_license.Enabled) return null;
        var groups = await _groups.GetGroupsAsync(owner, ct);
        var applicable = await _limits.ApplicableAsync(owner, groups, ct);
        UsageLimit? strictest = null;
        foreach (var row in applicable)
            if (strictest is null || row.LimitUsd < strictest.LimitUsd)
                strictest = new UsageLimit(row.LimitUsd, row.Scope);
        return strictest;
    }
}

/// <summary>
/// Grants admin rights to members of groups mapped to the admin role (groups come from the
/// OAuth token). License-gated like every enterprise feature.
/// </summary>
public sealed class GroupRoleAdminProvider : IAdminRoleProvider
{
    private readonly IEnterpriseLicense _license;
    private readonly UserGroupStore _groups;

    public GroupRoleAdminProvider(IEnterpriseLicense license, UserGroupStore groups)
    {
        _license = license;
        _groups = groups;
    }

    public async Task<bool> IsAdminAsync(string owner, CancellationToken ct = default)
        => _license.Enabled && await _groups.IsInAdminGroupAsync(owner, ct);

    public async Task<bool> HasAdminMappingsAsync(CancellationToken ct = default)
        => _license.Enabled && await _groups.AnyAdminGroupAsync(ct);
}
