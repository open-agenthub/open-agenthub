using System.Text.Json;

namespace AgentHub.Api.Services;

/// <summary>
/// Which of the owner's secrets a session receives (docs/session-secrets.md).
///
/// A project selects secrets; a session follows that selection (stored as null) or narrows it to
/// an explicit list. A session outside a project gets nothing unless it names secrets itself.
/// The default is the opposite of the git token selection, where null means "every stored PAT":
/// a PAT is only ever offered to the host it belongs to, a secret is handed to the agent outright,
/// so "everything, including what is stored later" is not a default anyone should get unasked.
/// </summary>
public static class SessionSecretSelection
{
    /// <summary>Accepted wherever a selection is written as "follow the project", for the same
    /// reason the git token selection accepts it: an update has no other way back to null.</summary>
    public const string FollowProject = "*";

    /// <summary>
    /// A requested list checked against the stored secrets: null or the wildcard yield null, an
    /// unknown id is refused while the caller is still there to be told.
    /// </summary>
    public static IReadOnlyList<string>? Normalize(IEnumerable<string>? requested, IReadOnlyList<SessionSecretStore.Entry> stored)
    {
        if (requested is null) return null;
        var ids = Cleaned(requested);
        if (ids.Contains(FollowProject)) return null;
        return Known(ids, stored.Select(e => e.Id).ToHashSet());
    }

    /// <summary>A project's selection: every id must be stored, the wildcard has no meaning here.</summary>
    public static IReadOnlyList<string> NormalizeProject(IEnumerable<string> requested, IReadOnlyCollection<string> storedIds)
        => Known(Cleaned(requested), storedIds);

    private static List<string> Cleaned(IEnumerable<string> requested) =>
        requested.Select(raw => raw?.Trim() ?? "").Where(id => id.Length > 0).Distinct().ToList();

    private static List<string> Known(List<string> ids, IReadOnlyCollection<string> storedIds)
    {
        var unknown = ids.FirstOrDefault(id => !storedIds.Contains(id));
        return unknown is null ? ids : throw new ArgumentException($"No stored secret with id '{unknown}'.");
    }

    /// <summary>
    /// Refuses an explicit list that reaches beyond the session's project. Refused rather than
    /// cut down to the project's set: a caller that asked for a secret and silently got a session
    /// without it would find out only when the agent fails on the missing variable.
    /// </summary>
    public static void EnsureWithinProject(IReadOnlyList<string>? selected, string? projectId, IReadOnlyList<string> projectIds)
    {
        if (selected is null || projectId is null) return;
        var outside = selected.FirstOrDefault(id => !projectIds.Contains(id));
        if (outside is not null)
            throw new ArgumentException($"The secret '{outside}' is not selected for the session's project.");
    }

    /// <summary>
    /// The ids a session receives at this moment. Read at every start and whenever a secret, the
    /// project or the session changes, so a narrowed project takes effect on its running sessions
    /// too. An id whose secret has been removed is skipped.
    /// </summary>
    public static IReadOnlyList<string> Effective(IReadOnlyList<string>? selected, string? projectId,
        IReadOnlyList<string> projectIds, IReadOnlyList<SessionSecretStore.Entry> stored,
        IReadOnlyList<string>? granted = null)
    {
        IEnumerable<string> ids = selected
            ?? (projectId is null ? Array.Empty<string>() : projectIds);
        // A project narrowed after the session was created takes the secret away from it; a
        // selection can only lose entries this way, never gain them.
        if (selected is not null && projectId is not null) ids = ids.Where(projectIds.Contains);
        // A grant is the owner answering this session's own request: it holds whatever the project
        // selects, because the owner gave it to this session and no other.
        ids = ids.Concat(granted ?? Array.Empty<string>());
        return ids.Where(id => stored.Any(e => e.Id == id)).Distinct().ToList();
    }

    public static string? Serialize(IReadOnlyList<string>? ids) => ids is null ? null : JsonSerializer.Serialize(ids);

    /// <summary>Reads a stored selection. Malformed JSON is "nothing": unlike the git token
    /// selection, the safe reading of an unreadable column here is the empty one.</summary>
    public static IReadOnlyList<string>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return Array.Empty<string>(); }
    }

    /// <summary>A project's stored list; null and malformed are both "none".</summary>
    public static IReadOnlyList<string> ParseProject(string? json) => Parse(json) ?? Array.Empty<string>();
}
