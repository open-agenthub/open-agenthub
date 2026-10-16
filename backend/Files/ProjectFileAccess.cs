using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Files;

/// <summary>
/// Decides which other sessions' files a session may read: the ones that share its owner and its
/// project (docs/project-files.md).
///
/// One class rather than a check in each route, because every route that hands out a sibling's
/// file has to agree on the rule. Split across a listing and a download, the first copy to drift
/// would list a file the other refuses, or — the direction that matters — serve one the listing
/// would never have shown.
/// </summary>
public interface IProjectFileAccess
{
    /// <summary>The sessions whose files <paramref name="caller"/> may read, never itself.</summary>
    Task<IReadOnlyList<SessionRecord>> SiblingsAsync(
        SessionRecord caller, CancellationToken ct = default);

    /// <summary>
    /// The session with <paramref name="sessionId"/> if <paramref name="caller"/> may read its
    /// files. Null covers a missing session and a refused one alike, so a caller cannot tell an
    /// id that exists in another project or under another owner from one that never existed.
    /// </summary>
    Task<SessionRecord?> ResolveSiblingAsync(
        SessionRecord caller, string sessionId, CancellationToken ct = default);
}

public sealed class ProjectFileAccess(ISessionStore sessions, ISessionShareStatus shares)
    : IProjectFileAccess
{
    public async Task<IReadOnlyList<SessionRecord>> SiblingsAsync(
        SessionRecord caller, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(caller.ProjectId) || await IsSharedAsync(caller, ct)) return [];
        return (await sessions.ListAsync(caller.Owner, ct))
            .Where(candidate => IsSibling(caller, candidate))
            .ToArray();
    }

    public async Task<SessionRecord?> ResolveSiblingAsync(
        SessionRecord caller, string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(caller.ProjectId) || string.IsNullOrEmpty(sessionId) ||
            await IsSharedAsync(caller, ct))
        {
            return null;
        }
        var target = await sessions.GetAsync(caller.Owner, sessionId, ct);
        return target is not null && IsSibling(caller, target) ? target : null;
    }

    /// <summary>
    /// A session somebody else can open reads no sibling files at all. A share is granted for one
    /// session; a collaborator types into its terminal and a viewer watches it, so an agent that
    /// could fetch a sibling's file on request would hand the share's recipient files from
    /// sessions that were never shared with them. Any share counts, not only a collaborator's:
    /// the rule then has no role to get wrong, and a viewer upgraded later changes nothing.
    ///
    /// Asked on every call rather than cached on the session, so sharing a session takes effect
    /// for the pod that is already running.
    /// </summary>
    private Task<bool> IsSharedAsync(SessionRecord caller, CancellationToken ct) =>
        shares.IsSharedAsync(caller.Id, ct);

    /// <summary>
    /// The rule between two sessions. The owner is compared here even though both lookups above are already
    /// scoped to it: the comparison is the boundary, and a store that one day answers across
    /// owners must not be able to widen it.
    ///
    /// A session without a project has no siblings. Treating "no project" as a project of its own
    /// would join every unsorted session an owner has ever started into one pool, which is the
    /// opposite of what leaving a session out of a project says.
    /// </summary>
    public static bool IsSibling(SessionRecord caller, SessionRecord candidate) =>
        !string.IsNullOrEmpty(caller.ProjectId) &&
        string.Equals(caller.ProjectId, candidate.ProjectId, StringComparison.Ordinal) &&
        string.Equals(caller.Owner, candidate.Owner, StringComparison.Ordinal) &&
        !string.Equals(caller.Id, candidate.Id, StringComparison.Ordinal);
}
