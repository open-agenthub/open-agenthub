using System.Text.Json;

namespace AgentHub.Api.Services;

/// <summary>
/// Which of the owner's stored git PATs a session is built with (docs/credential-scopes.md).
///
/// A session gets at most one token per host. Git's credential store answers with the first line
/// matching a host, so with two tokens for one host in the pod the one used would depend only on
/// the order the list happened to be in.
///
/// Null is "the default token of every host, including hosts added later". Before a host could
/// carry several tokens this was "every stored PAT"; with one token per host the two are the same
/// set, which is why an omitted field still means it. An empty list is "none". The wildcard
/// <see cref="All"/> is accepted as a spelling of null wherever a selection is written, because an
/// update has no other way back to the defaults: null already means "unchanged" there.
/// </summary>
public static class GitPatSelection
{
    public const string All = "*";

    /// <summary>
    /// Normalizes a requested selection against the stored list: null or a wildcard yields null
    /// (defaults), otherwise the trimmed, de-duplicated ids in request order. An unknown id, or two
    /// ids for one host, is refused here, while the caller is still around to be told, rather than
    /// resolved at spawn time where the only symptom would be a push under the wrong identity or a
    /// clone that fails on authentication.
    /// </summary>
    public static IReadOnlyList<string>? Normalize(IEnumerable<string>? requested, IReadOnlyList<GitPatStore.Entry> stored)
    {
        if (requested is null) return null;
        var ids = new List<string>();
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in requested)
        {
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            if (id == All) return null;
            var entry = stored.FirstOrDefault(e => e.Id == id)
                ?? throw new ArgumentException($"No stored git token with id '{id}'.");
            if (ids.Contains(id)) continue;
            if (hosts.TryGetValue(entry.Host, out var other))
                throw new ArgumentException(
                    $"A session uses one git token per host; '{other}' and '{id}' are both for {entry.Host}.");
            hosts[entry.Host] = id;
            ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// The stored entries the selection keeps, in stored order and never more than one per host.
    /// Null gives every host's default. An id whose PAT has since been removed is skipped: a PAT is
    /// one of possibly several credentials for a clone, and refusing to start the session over a
    /// token that no longer exists would help nobody. Falling back to the host's default instead
    /// was rejected — a session pointed at a group token must not quietly start pushing as its
    /// owner because the group token was deleted.
    /// </summary>
    public static IReadOnlyList<GitPatStore.Entry> Apply(IReadOnlyList<GitPatStore.Entry> stored, IReadOnlyList<string>? selected)
    {
        if (selected is null) return GitPatStore.Defaults(stored);
        // Selections are checked when written, but one stored by an older backend never was; the
        // first stored entry of a host wins rather than both landing in the pod.
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return stored.Where(e => selected.Contains(e.Id) && hosts.Add(e.Host)).ToList();
    }

    public static string? Serialize(IReadOnlyList<string>? ids) =>
        ids is null ? null : JsonSerializer.Serialize(ids);

    /// <summary>Reads a stored selection; malformed JSON counts as the defaults so a corrupt column
    /// cannot stop a session from starting.</summary>
    public static IReadOnlyList<string>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<string>>(json); }
        catch (JsonException) { return null; }
    }
}
