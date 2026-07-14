namespace AgentHub.Api.Admin;

/// <summary>
/// Decides who may reach the admin area. Admins are listed in <c>Ee:Admins</c>
/// (comma/space/semicolon separated owner usernames). If the list is empty the
/// instance runs in bootstrap mode where every signed-in user is an admin — handy
/// for a fresh self-hosted deployment, but a warning is logged so operators lock it down.
/// </summary>
public sealed class AdminAccess
{
    private readonly HashSet<string> _admins;
    public bool Bootstrap { get; }

    public AdminAccess(IConfiguration cfg, ILogger<AdminAccess> log)
    {
        var raw = cfg["Ee:Admins"] ?? "";
        _admins = raw.Split(new[] { ',', ';', ' ', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Bootstrap = _admins.Count == 0;
        if (Bootstrap)
            log.LogWarning("Ee:Admins is empty — every signed-in user is an admin. Set Ee:Admins to lock the admin area down.");
    }

    public bool IsAdmin(string? owner)
        => !string.IsNullOrEmpty(owner) && (Bootstrap || _admins.Contains(owner));
}
