using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>Inherited access remains tied to its parent so revoking a grant cannot leave a
/// credential behind in an unattended descendant. A direct owner grant is independent.</summary>
public sealed class SessionSecretAccessResolver(
    IReadOnlyDictionary<string, SessionRecord> sessions,
    IReadOnlyDictionary<string, IReadOnlyList<string>> projects,
    IReadOnlyCollection<string> storedIds)
{
    public IReadOnlyList<string> Selected(SessionRecord session)
    {
        IReadOnlyList<string> project = session.ProjectId is { } id && projects.TryGetValue(id, out var ids) ? ids : [];
        var selected = SessionSecretSelection.Parse(session.SecretIdsJson) ?? (session.ProjectId is null ? [] : project);
        return selected.Where(id => storedIds.Contains(id) && (session.ProjectId is null || project.Contains(id))).ToList();
    }

    public IReadOnlyList<string> Effective(SessionRecord session) => Effective(session, []);

    private IReadOnlyList<string> Effective(SessionRecord session, HashSet<string> path)
    {
        if (!path.Add(session.Id)) return [];
        var ids = Selected(session).Concat(SessionSecretGrants.Parse(session.SecretGrantIdsJson)
            .Where(g => g.Source != "inherited" || InheritedAllowed(g, path)).Select(g => g.SecretId));
        if (SessionSecretSelection.Parse(session.SecretCeilingJson) is { } ceiling) ids = ids.Where(ceiling.Contains);
        return ids.Where(storedIds.Contains).Distinct().ToList();
    }

    private bool InheritedAllowed(StoredSessionSecretGrant grant, HashSet<string> path)
    {
        if (grant.SourceSessionId is not { } id || !sessions.TryGetValue(id, out var parent)) return false;
        if (!Effective(parent, new HashSet<string>(path)).Contains(grant.SecretId)) return false;
        return SessionSecretGrants.Parse(parent.SecretGrantIdsJson)
            .Any(g => g.SecretId == grant.SecretId && g.InheritToChildren);
    }

    public IReadOnlyList<StoredSessionSecretGrant> ActiveGrants(SessionRecord session)
        => SessionSecretGrants.Parse(session.SecretGrantIdsJson)
            .Where(g => g.Source != "inherited" || InheritedAllowed(g, [session.Id])).ToList();
}
