namespace AgentHub.Api.Admin;

/// <summary>The role a group mapping assigns to an owner, and the groups it comes from.</summary>
public sealed record GroupRole(string Role, IReadOnlyList<string> Groups);

/// <summary>
/// Additional admin sources beyond the <c>Ee:Admins</c> config list — e.g. the enterprise
/// group-role mapping (groups from the OAuth token mapped to a role). Implementations
/// must self-gate (return null / false without a valid license).
/// </summary>
public interface IAdminRoleProvider
{
    /// <summary>
    /// The role the owner's groups map to: "admin" when any of them is an admin group, else
    /// "user" when any is a user group, else null.
    /// </summary>
    Task<GroupRole?> GetGroupRoleAsync(string owner, CancellationToken ct = default);

    /// <summary>True when the provider designates at least one admin (disables bootstrap mode).</summary>
    Task<bool> HasAdminMappingsAsync(CancellationToken ct = default);
}

/// <summary>Where an owner's effective role comes from, in precedence order.</summary>
public static class RoleSources
{
    /// <summary>Listed in <c>Ee:Admins</c>: a break-glass admin the UI cannot demote.</summary>
    public const string Values = "values";
    /// <summary>Set for this user in the admin area.</summary>
    public const string User = "user";
    /// <summary>From an OIDC group mapped to a role (Enterprise Edition).</summary>
    public const string Group = "group";
    /// <summary>No source designates any admin, so everyone is one.</summary>
    public const string Bootstrap = "bootstrap";
    /// <summary>Nothing applies: a regular user.</summary>
    public const string Default = "default";
}

/// <summary>An owner's effective role and the source that decided it.</summary>
public sealed record RoleResolution(string Role, string Source, IReadOnlyList<string> Groups)
{
    public bool IsAdmin => Role == UserRoleStore.AdminRole;
}

/// <summary>
/// Decides who may reach the admin area. Precedence, first match wins:
/// <list type="number">
/// <item><c>Ee:Admins</c> (comma/space/semicolon separated owner usernames) — always admin.</item>
/// <item>A role set for the user in the admin area (<see cref="IUserRoleStore"/>).</item>
/// <item>An admin group mapping from an <see cref="IAdminRoleProvider"/> (EE).</item>
/// <item>Bootstrap: while no source designates any admin, every signed-in user is one.</item>
/// <item>Otherwise a regular user ("user" group mappings only label this).</item>
/// </list>
/// Nothing is cached: a role changed in the UI applies at the user's next request, on every
/// replica, which is what the change replaces a redeploy for. See docs/admin-roles.md.
/// </summary>
public sealed class AdminAccess
{
    private readonly HashSet<string> _admins;
    private readonly IEnumerable<IAdminRoleProvider> _roleProviders;
    private readonly IUserRoleStore? _userRoles;

    /// <summary>True when no admins are configured via <c>Ee:Admins</c>. Explicit user roles and
    /// role providers can still end bootstrap behavior at evaluation time
    /// (see <see cref="IsBootstrapActiveAsync"/>).</summary>
    public bool Bootstrap { get; }

    /// <summary>The <c>Ee:Admins</c> entries, for display as break-glass accounts.</summary>
    public IReadOnlyCollection<string> ValuesAdmins => _admins;

    public AdminAccess(IConfiguration cfg, ILogger<AdminAccess> log,
        IEnumerable<IAdminRoleProvider> roleProviders, IUserRoleStore? userRoles = null)
    {
        _roleProviders = roleProviders;
        _userRoles = userRoles;
        var raw = cfg["Ee:Admins"] ?? "";
        _admins = raw.Split(new[] { ',', ';', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Bootstrap = _admins.Count == 0;
        if (Bootstrap)
            log.LogWarning("Ee:Admins is empty — every signed-in user is an admin until someone is made an admin in the admin area or a group is mapped to the admin role. Set Ee:Admins or designate an admin to lock the admin area down.");
    }

    public bool IsValuesAdmin(string? owner) => !string.IsNullOrEmpty(owner) && _admins.Contains(owner);

    /// <summary>True while no source designates any admin — every signed-in user is one.</summary>
    public async Task<bool> IsBootstrapActiveAsync(CancellationToken ct = default)
    {
        if (!Bootstrap) return false;
        if (_userRoles is not null && await _userRoles.AnyAdminAsync(ct)) return false;
        foreach (var provider in _roleProviders)
            if (await provider.HasAdminMappingsAsync(ct)) return false;
        return true;
    }

    public async Task<bool> IsAdminAsync(string? owner, CancellationToken ct = default)
        => !string.IsNullOrEmpty(owner) && (await ResolveAsync(owner, ct)).IsAdmin;

    public async Task<RoleResolution> ResolveAsync(string owner, CancellationToken ct = default)
    {
        if (IsValuesAdmin(owner)) return new(UserRoleStore.AdminRole, RoleSources.Values, []);
        var explicitRole = _userRoles is null ? null : await _userRoles.GetRoleAsync(owner, ct);
        return await ResolveAsync(owner, explicitRole, bootstrapActive: null, ct);
    }

    /// <summary>
    /// Resolution with the explicit role and bootstrap state supplied by the caller, so listing
    /// every user reads the roles table and the bootstrap sources once rather than per user.
    /// </summary>
    public async Task<RoleResolution> ResolveAsync(string owner, string? explicitRole, bool? bootstrapActive,
        CancellationToken ct = default)
    {
        if (IsValuesAdmin(owner)) return new(UserRoleStore.AdminRole, RoleSources.Values, []);
        if (explicitRole is not null) return new(explicitRole, RoleSources.User, []);

        GroupRole? userGroups = null;
        foreach (var provider in _roleProviders)
        {
            var mapped = await provider.GetGroupRoleAsync(owner, ct);
            if (mapped is null) continue;
            if (mapped.Role == UserRoleStore.AdminRole)
                return new(UserRoleStore.AdminRole, RoleSources.Group, mapped.Groups);
            userGroups ??= mapped;
        }

        if (bootstrapActive ?? await IsBootstrapActiveAsync(ct))
            return new(UserRoleStore.AdminRole, RoleSources.Bootstrap, []);
        return userGroups is not null
            ? new(UserRoleStore.UserRole, RoleSources.Group, userGroups.Groups)
            : new(UserRoleStore.UserRole, RoleSources.Default, []);
    }

    /// <summary>
    /// Checked after a role change: true when an admin remains. A values admin always counts (it
    /// cannot be demoted in the UI); otherwise one of the known users has to resolve to admin.
    /// A change that drops the instance back into bootstrap counts as removing the last admin
    /// even though everyone then resolves to admin: demoting the last designated admin must
    /// not quietly hand the admin area to every signed-in user.
    /// </summary>
    public async Task<bool> KeepsAnAdminAsync(bool bootstrapBefore, IEnumerable<string> knownOwners,
        CancellationToken ct = default)
    {
        if (_admins.Count > 0) return true;
        var bootstrap = await IsBootstrapActiveAsync(ct);
        if (bootstrap && !bootstrapBefore) return false;
        IReadOnlyDictionary<string, string> roles = _userRoles is null
            ? new Dictionary<string, string>()
            : await _userRoles.ListRolesAsync(ct);
        foreach (var owner in knownOwners)
        {
            roles.TryGetValue(owner, out var explicitRole);
            if ((await ResolveAsync(owner, explicitRole, bootstrap, ct)).IsAdmin) return true;
        }
        return false;
    }
}
