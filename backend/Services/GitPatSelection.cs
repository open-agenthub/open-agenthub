using System.Text.Json;

namespace AgentHub.Api.Services;

/// <summary>
/// Which of the owner's stored git PATs a session is built with (docs/credential-scopes.md).
///
/// Null is "every stored PAT, including ones added later" — the behaviour every session had before
/// a selection existed, and therefore what an omitted field means. An empty list is "none". The
/// wildcard <see cref="All"/> is accepted as a spelling of null wherever a selection is written,
/// because an update has no other way back to "all": null already means "unchanged" there.
/// </summary>
public static class GitPatSelection
{
    public const string All = "*";

    /// <summary>
    /// Normalizes a requested selection against the stored list: null or a wildcard yields null
    /// (all), otherwise the trimmed, de-duplicated ids in request order. An unknown id is refused
    /// here, while the caller is still around to be told, rather than silently dropped at spawn
    /// time where the only symptom would be a clone that fails on authentication.
    /// </summary>
    public static IReadOnlyList<string>? Normalize(IEnumerable<string>? requested, IReadOnlyList<GitPatStore.Entry> stored)
    {
        if (requested is null) return null;
        var ids = new List<string>();
        foreach (var raw in requested)
        {
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            if (id == All) return null;
            if (!stored.Any(e => e.Id == id))
                throw new ArgumentException($"No stored git token with id '{id}'.");
            if (!ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>The stored entries the selection keeps. An id whose PAT has since been removed is
    /// skipped: a PAT is one of possibly several credentials for a clone, and refusing to start the
    /// session over a token that no longer exists would help nobody.</summary>
    public static IReadOnlyList<GitPatStore.Entry> Apply(IReadOnlyList<GitPatStore.Entry> stored, IReadOnlyList<string>? selected)
        => selected is null ? stored : stored.Where(e => selected.Contains(e.Id)).ToList();

    public static string? Serialize(IReadOnlyList<string>? ids) =>
        ids is null ? null : JsonSerializer.Serialize(ids);

    /// <summary>Reads a stored selection; malformed JSON counts as "all" so a corrupt column cannot
    /// stop a session from starting.</summary>
    public static IReadOnlyList<string>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<List<string>>(json); }
        catch (JsonException) { return null; }
    }
}
