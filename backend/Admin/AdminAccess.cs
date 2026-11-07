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

    /// <summary>True when the provider designates at least one admin.</summary>
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
/// <item>Otherwise a regular user ("user" group mappings only label this).</item>
/// </list>
/// Nobody is an admin by default. While no source designates one, the admin area is closed to
/// everyone and the app says so (<see cref="IsAdminDesignatedAsync"/>).
/// Nothing is cached: a role changed in the UI applies at the user's next request, on every
/// replica, which is what the change replaces a redeploy for. See docs/admin-roles.md.
/// </summary>
public sealed class AdminAccess
{
    private readonly HashSet<string> _admins;
    private readonly IEnumerable<IAdminRoleProvider> _roleProviders;
    private readonly IUserRoleStore? _userRoles;

    /// <summary>The <c>Ee:Admins</c> entries, for display as break-glass accounts.</summary>
    public IReadOnlyCollection<string> ValuesAdmins => _admins;

    public AdminAccess(IConfiguration cfg, ILogger<AdminAccess> log,
        IEnumerable<IAdminRoleProvider> roleProviders, IUserRoleStore? userRoles = null,
        AgentHub.Api.Auth.HubAuthOptions? hubAuth = null)
    {
        _roleProviders = roleProviders;
        _userRoles = userRoles;
        var raw = cfg["Ee:Admins"] ?? "";
        _admins = raw.Split(new[] { ',', ';', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The key holder is the install's only interactive user. Left to Ee:Admins, an install
        // that lists someone else there would lock its own owner out of the admin area, and one
        // that lists nobody would have no admin at all. Insecure development mode has exactly one
        // user too, and is documented as "anyone who can reach it is an admin".
        if (hubAuth?.Mode == AgentHub.Api.Auth.HubAuthMode.ApiKey)
            _admins.Add(hubAuth.ApiKeyOwner);
        else if (hubAuth?.Mode == AgentHub.Api.Auth.HubAuthMode.InsecureDev)
            _admins.Add(AgentHub.Api.Auth.HubAuthOptions.InsecureDevUser);
        if (_admins.Count == 0)
            log.LogWarning("Ee:Admins (Helm ee.admins) is empty. Unless an admin was designated in the admin area or through an admin group, nobody can open the admin area: set ee.admins to the preferred_username of the first administrator. See docs/admin-bootstrap.md.");
    }

    public bool IsValuesAdmin(string? owner) => !string.IsNullOrEmpty(owner) && _admins.Contains(owner);

    /// <summary>
    /// True when any source designates an admin. False means nobody can reach the admin area:
    /// the app shows every signed-in user how to designate one (docs/admin-bootstrap.md).
    /// </summary>
    public async Task<bool> IsAdminDesignatedAsync(CancellationToken ct = default)
    {
        if (_admins.Count > 0) return true;
        if (_userRoles is not null && await _userRoles.AnyAdminAsync(ct)) return true;
        foreach (var provider in _roleProviders)
            if (await provider.HasAdminMappingsAsync(ct)) return true;
        return false;
    }

    public async Task<bool> IsAdminAsync(string? owner, CancellationToken ct = default)
        => !string.IsNullOrEmpty(owner) && (await ResolveAsync(owner, ct)).IsAdmin;

    public async Task<RoleResolution> ResolveAsync(string owner, CancellationToken ct = default)
    {
        if (IsValuesAdmin(owner)) return new(UserRoleStore.AdminRole, RoleSources.Values, []);
        var explicitRole = _userRoles is null ? null : await _userRoles.GetRoleAsync(owner, ct);
        return await ResolveAsync(owner, explicitRole, ct);
    }

    /// <summary>
    /// Resolution with the explicit role supplied by the caller, so listing every user reads the
    /// roles table once rather than per user.
    /// </summary>
    public async Task<RoleResolution> ResolveAsync(string owner, string? explicitRole, CancellationToken ct = default)
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

        return userGroups is not null
            ? new(UserRoleStore.UserRole, RoleSources.Group, userGroups.Groups)
            : new(UserRoleStore.UserRole, RoleSources.Default, []);
    }

    /// <summary>
    /// Checked after a role change: true when an admin remains. A values admin always counts (it
    /// cannot be demoted in the UI); otherwise one of the known users has to resolve to admin.
    /// Without that check an admin could demote the last admin and close the admin area until
    /// someone edits the Helm values.
    /// </summary>
    public async Task<bool> KeepsAnAdminAsync(IEnumerable<string> knownOwners, CancellationToken ct = default)
    {
        if (_admins.Count > 0) return true;
        IReadOnlyDictionary<string, string> roles = _userRoles is null
            ? new Dictionary<string, string>()
            : await _userRoles.ListRolesAsync(ct);
        foreach (var owner in knownOwners)
        {
            roles.TryGetValue(owner, out var explicitRole);
            if ((await ResolveAsync(owner, explicitRole, ct)).IsAdmin) return true;
        }
        return false;
    }
}
