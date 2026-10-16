using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// The list of git personal access tokens in a user's credential secret.
///
/// The secret used to have one fixed slot per provider kind (<c>gitlab_token</c>/<c>gitlab_host</c>,
/// <c>github_token</c>/<c>github_host</c>), which cannot hold a second GitLab host at all — a user
/// with a company instance and a personal one had to pick. The list lives in one secret key,
/// <c>git_pats</c>, as a JSON array of <c>{id, kind, host, token}</c>.
///
/// The legacy slots are migrated lazily: every read folds them into the list, and the next write of
/// the secret removes them. Reading never writes, so a user who only ever looks at their status
/// page keeps a secret that any older backend still understands. The migrated entries get fixed
/// ids rather than random ones, because the status page shows ids before any write has happened
/// and a <c>DELETE</c> for a random id would otherwise miss on the next read.
/// </summary>
public static class GitPatStore
{
    public const string Key = "git_pats";

    /// <summary>
    /// Generous for a person, but a bound: the whole list is one secret key, and a Kubernetes
    /// secret is capped at 1 MiB in total. Without a limit a scripted caller could grow it until
    /// every credential write for that user fails.
    /// </summary>
    public const int MaxEntries = 32;

    public const string LegacyGitLabId = "legacy-gitlab";
    public const string LegacyGitHubId = "legacy-github";

    private static readonly string[] LegacyKeys =
        ["gitlab_token", "gitlab_host", "github_token", "github_host"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public sealed record Entry(string Id, string Kind, string Host, string Token);

    /// <summary>
    /// The stored list plus any legacy slots that have not been written away yet. Malformed JSON
    /// yields an empty list rather than an exception: the value was validated when stored, so a
    /// bad document is corruption, and refusing to read it would stop every session of that user.
    /// </summary>
    public static IReadOnlyList<Entry> Read(IDictionary<string, byte[]>? data)
    {
        if (data is null) return Array.Empty<Entry>();
        var entries = new List<Entry>();
        if (data.TryGetValue(Key, out var raw))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<Entry>>(raw, Json);
                if (parsed is not null)
                    entries.AddRange(parsed.Where(e =>
                        !string.IsNullOrEmpty(e.Id) && GitPatKind.IsValid(e.Kind)
                        && ManualGitCredentials.IsValidHost(e.Host) && ManualGitCredentials.IsValidToken(e.Token)));
            }
            catch (JsonException)
            {
                // Corrupt list: fall through to whatever the legacy slots still hold.
            }
        }

        AddLegacy(entries, data, "gitlab", GitPatKind.GitLab, LegacyGitLabId, ManualGitCredentials.DefaultGitLabHost);
        AddLegacy(entries, data, "github", GitPatKind.GitHub, LegacyGitHubId, ManualGitCredentials.DefaultGitHubHost);
        return entries;
    }

    /// <summary>True when the secret still carries slots that <see cref="Write"/> would fold in.</summary>
    public static bool HasLegacySlots(IDictionary<string, byte[]> data) =>
        LegacyKeys.Any(data.ContainsKey);

    /// <summary>Replaces the list and drops the legacy slots it now represents.</summary>
    public static void Write(IDictionary<string, byte[]> data, IReadOnlyList<Entry> entries)
    {
        foreach (var key in LegacyKeys) data.Remove(key);
        if (entries.Count == 0) data.Remove(Key);
        else data[Key] = JsonSerializer.SerializeToUtf8Bytes(entries, Json);
    }

    /// <summary>
    /// Adds a token for a host, or replaces the token of the entry already stored for that host.
    /// Validation happens here, at the edge the user is looking at, for the same reason as the
    /// other credential fields: a newline in a token would append a store line for an arbitrary
    /// host, and a session failing hours later gives nobody anything to act on.
    /// </summary>
    public static Entry Upsert(IDictionary<string, byte[]> data, UpsertGitPatRequest request)
    {
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (!GitPatKind.IsValid(kind))
            throw new ArgumentException("The token kind must be 'gitlab' or 'github'.");
        var host = request.Host?.Trim().ToLowerInvariant();
        if (!ManualGitCredentials.IsValidHost(host))
            throw new ArgumentException(
                "A token needs the host it belongs to (e.g. gitlab.example.com): a hostname with an "
                + "optional port, without a scheme or path. The token is only ever sent to that host.");
        if (!ManualGitCredentials.IsValidToken(request.Token))
            throw new ArgumentException(
                "The token must not contain whitespace or control characters and is limited to "
                + $"{ManualGitCredentials.MaxTokenLength} characters.");

        var entries = Read(data).ToList();
        var index = entries.FindIndex(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase));
        Entry entry;
        if (index >= 0)
        {
            // Same id on rotation, so a client that listed the entries before the rotation can
            // still remove this one afterwards.
            entry = entries[index] with { Kind = kind!, Host = host!, Token = request.Token! };
            entries[index] = entry;
        }
        else
        {
            if (entries.Count >= MaxEntries)
                throw new ArgumentException($"At most {MaxEntries} git tokens can be stored.");
            entry = new Entry(Guid.NewGuid().ToString("N"), kind!, host!, request.Token!);
            entries.Add(entry);
        }
        Write(data, entries);
        return entry;
    }

    /// <summary>Removes an entry; false when no entry had that id. Idempotent either way.</summary>
    public static bool Remove(IDictionary<string, byte[]> data, string id)
    {
        var entries = Read(data);
        var kept = entries.Where(e => e.Id != id).ToList();
        var removed = kept.Count != entries.Count;
        if (removed || HasLegacySlots(data)) Write(data, kept);
        return removed;
    }

    private static void AddLegacy(List<Entry> entries, IDictionary<string, byte[]> data,
        string prefix, string kind, string id, string defaultHost)
    {
        var token = Value(data, $"{prefix}_token");
        if (!ManualGitCredentials.IsValidToken(token)) return;
        // A token stored before hosts existed has none; it was used against the public instance,
        // so that is the host it migrates with.
        var host = Value(data, $"{prefix}_host")?.Trim();
        var effectiveHost = string.IsNullOrWhiteSpace(host) ? defaultHost : host.ToLowerInvariant();
        if (!ManualGitCredentials.IsValidHost(effectiveHost)) return;
        // A list entry for the same host was stored after the migration started; it is the newer
        // one and wins.
        if (entries.Any(e => string.Equals(e.Host, effectiveHost, StringComparison.OrdinalIgnoreCase))) return;
        entries.Add(new Entry(id, kind, effectiveHost, token!));
    }

    private static string? Value(IDictionary<string, byte[]> data, string key) =>
        data.TryGetValue(key, out var raw) ? Encoding.UTF8.GetString(raw) : null;
}
