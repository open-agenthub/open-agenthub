namespace AgentHub.Api.Admin;

/// <summary>
/// Additional admin sources beyond the <c>Ee:Admins</c> config list — e.g. the enterprise
/// group-role mapping (groups from the OAuth token mapped to the admin role). Implementations
/// must self-gate (return false without a valid license).
/// </summary>
public interface IAdminRoleProvider
{
    /// <summary>True when the provider grants the owner admin rights.</summary>
    Task<bool> IsAdminAsync(string owner, CancellationToken ct = default);

    /// <summary>True when the provider designates at least one admin (disables bootstrap mode).</summary>
    Task<bool> HasAdminMappingsAsync(CancellationToken ct = default);
}

/// <summary>
/// Decides who may reach the admin area. Admins are listed in <c>Ee:Admins</c>
/// (comma/space/semicolon separated owner usernames) and/or granted through
/// <see cref="IAdminRoleProvider"/>s (EE group-role mapping). If neither source
/// designates any admin, the instance runs in bootstrap mode where every signed-in
/// user is an admin — handy for a fresh self-hosted deployment, but a warning is
/// logged so operators lock it down.
/// </summary>
public sealed class AdminAccess
{
    private readonly HashSet<string> _admins;
    private readonly IEnumerable<IAdminRoleProvider> _roleProviders;

    /// <summary>True when no admins are configured via <c>Ee:Admins</c>. Role providers can
    /// still disable bootstrap behavior at evaluation time (see <see cref="IsAdminAsync"/>).</summary>
    public bool Bootstrap { get; }

    public AdminAccess(IConfiguration cfg, ILogger<AdminAccess> log,
        IEnumerable<IAdminRoleProvider> roleProviders)
    {
        _roleProviders = roleProviders;
        var raw = cfg["Ee:Admins"] ?? "";
        _admins = raw.Split(new[] { ',', ';', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Bootstrap = _admins.Count == 0;
        if (Bootstrap)
            log.LogWarning("Ee:Admins is empty — every signed-in user is an admin until an admin group role is mapped. Set Ee:Admins or map a group to the admin role to lock the admin area down.");
    }

    public async Task<bool> IsAdminAsync(string? owner, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(owner)) return false;
        if (_admins.Contains(owner)) return true;
        foreach (var provider in _roleProviders)
            if (await provider.IsAdminAsync(owner, ct)) return true;
        if (!Bootstrap) return false;

        // Bootstrap mode: everyone is admin — but only while NO source designates admins.
        // As soon as a group is mapped to the admin role, membership decides.
        foreach (var provider in _roleProviders)
            if (await provider.HasAdminMappingsAsync(ct)) return false;
        return true;
    }
}
