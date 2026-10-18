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
/// <c>git_pats</c>, as a JSON array of <c>{id, kind, host, token, label, default, authorName,
/// authorEmail}</c>. A host may carry several tokens — a personal one next to a group or project
/// token — and exactly one of them is that host's default (docs/git-credentials.md).
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

    /// <summary>Longest label and author field; a guard for the listing and for the git config the
    /// author ends up in, not a rule anyone is expected to meet.</summary>
    public const int MaxLabelLength = 80;
    public const int MaxAuthorLength = 254;

    public const string LegacyGitLabId = "legacy-gitlab";
    public const string LegacyGitHubId = "legacy-github";

    private static readonly string[] LegacyKeys =
        ["gitlab_token", "gitlab_host", "github_token", "github_host"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public sealed record Entry(string Id, string Kind, string Host, string Token, string? Label = null,
        bool Default = false, string? AuthorName = null, string? AuthorEmail = null)
    {
        /// <summary>What the API shows of an entry: everything but the token.</summary>
        public GitPatInfo Info() => new(Id, Kind, Host, Label, Default, AuthorName, AuthorEmail);
    }

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
        return WithOneDefaultPerHost(entries);
    }

    /// <summary>The default entry of every host, in list order.</summary>
    public static IReadOnlyList<Entry> Defaults(IReadOnlyList<Entry> entries) =>
        entries.Where(e => e.Default).ToList();

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
    /// Adds a token, or changes one already stored. Which entry a request means:
    /// <list type="bullet">
    /// <item>an <c>id</c> names it directly — the only way to relabel an entry, change its author or
    /// make it the default without sending the token again;</item>
    /// <item>without an id, an entry with the same host <em>and</em> label is rotated in place. That
    /// keeps the old "same host replaces" behaviour for a client that sends no label, while a
    /// second label for the host is a second token;</item>
    /// <item>anything else is a new entry, and the host's default when it is the host's first.</item>
    /// </list>
    /// Validation happens here, at the edge the user is looking at, for the same reason as the
    /// other credential fields: a newline in a token would append a store line for an arbitrary
    /// host, and a session failing hours later gives nobody anything to act on.
    /// </summary>
    public static Entry Upsert(IDictionary<string, byte[]> data, UpsertGitPatRequest request)
    {
        var entries = Read(data).ToList();
        var label = OptionalText(request.Label, MaxLabelLength, "label");
        var authorName = OptionalText(request.AuthorName, MaxAuthorLength, "author name");
        var authorEmail = OptionalText(request.AuthorEmail, MaxAuthorLength, "author e-mail");
        // Both end up as a quoted value in a git config file; '<' and '>' would also break the
        // "Name <email>" line git writes into every commit.
        if (authorName is not null && authorName.Any(c => c is '<' or '>' or '"' or '\\'))
            throw new ArgumentException("The author name must not contain <, >, quotes or backslashes.");
        if (authorEmail is not null && (!authorEmail.Contains('@')
            || authorEmail.Any(c => char.IsWhiteSpace(c) || c is '<' or '>' or '"' or '\\')))
            throw new ArgumentException("The author e-mail must be a single address such as dev@example.com.");

        int index;
        string kind, host;
        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            var id = request.Id.Trim();
            index = entries.FindIndex(e => e.Id == id);
            if (index < 0) throw new ArgumentException($"No stored git token with id '{id}'.");
            // A session's selection is judged per host (one token per host). Letting an id move to
            // another host would silently move every session that picked it.
            kind = entries[index].Kind;
            host = entries[index].Host;
            if (request.Kind is not null && !string.Equals(request.Kind.Trim(), kind, StringComparison.OrdinalIgnoreCase)
                || request.Host is not null && !string.Equals(request.Host.Trim(), host, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The kind and host of a stored token cannot change; store a new token instead.");
        }
        else
        {
            kind = request.Kind?.Trim().ToLowerInvariant()!;
            if (!GitPatKind.IsValid(kind))
                throw new ArgumentException("The token kind must be 'gitlab' or 'github'.");
            host = request.Host?.Trim().ToLowerInvariant()!;
            if (!ManualGitCredentials.IsValidHost(host))
                throw new ArgumentException(
                    "A token needs the host it belongs to (e.g. gitlab.example.com): a hostname with an "
                    + "optional port, without a scheme or path. The token is only ever sent to that host.");
            index = entries.FindIndex(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Label ?? "", label ?? "", StringComparison.OrdinalIgnoreCase));
        }

        var token = request.Token;
        if (token is null ? index < 0 : !ManualGitCredentials.IsValidToken(token))
            throw new ArgumentException(
                "The token must not contain whitespace or control characters and is limited to "
                + $"{ManualGitCredentials.MaxTokenLength} characters.");

        if (index >= 0)
        {
            // Same id on rotation, so a session that picked this token keeps it and a client that
            // listed the entries before can still remove this one afterwards. A field left out of
            // the request is kept; an empty string clears the optional ones.
            var current = entries[index];
            entries[index] = current with
            {
                Token = token ?? current.Token,
                Label = request.Label is null ? current.Label : label,
                AuthorName = request.AuthorName is null ? current.AuthorName : authorName,
                AuthorEmail = request.AuthorEmail is null ? current.AuthorEmail : authorEmail
            };
        }
        else
        {
            if (entries.Count >= MaxEntries)
                throw new ArgumentException($"At most {MaxEntries} git tokens can be stored.");
            var firstForHost = !entries.Any(e => string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase));
            entries.Add(new Entry(Guid.NewGuid().ToString("N"), kind, host, token!, label, firstForHost,
                authorName, authorEmail));
            index = entries.Count - 1;
        }

        // Only "make this the default" is accepted. A host without a default would leave every
        // session that never chose a token for it with none, so the way off a default is to mark
        // another token of the host.
        if (request.Default == true)
            for (var i = 0; i < entries.Count; i++)
                if (string.Equals(entries[i].Host, host, StringComparison.OrdinalIgnoreCase))
                    entries[i] = entries[i] with { Default = i == index };
        Write(data, entries);
        return entries[index];
    }

    /// <summary>Removes an entry; false when no entry had that id. Idempotent either way.</summary>
    public static bool Remove(IDictionary<string, byte[]> data, string id)
    {
        var entries = Read(data);
        // Through the default rule again, so a removed default hands its role to the next token of
        // that host in the stored document and not only in memory.
        var kept = WithOneDefaultPerHost(entries.Where(e => e.Id != id).ToList());
        var removed = kept.Count != entries.Count;
        if (removed || HasLegacySlots(data)) Write(data, kept);
        return removed;
    }

    /// <summary>
    /// Every host ends up with exactly one default: the marked one, or the first stored when none
    /// is marked. The second case is the migration — a list written before labels existed held one
    /// entry per host and no flag, so each of those becomes its host's default without a write,
    /// and a session that never chose a token keeps the one it had.
    /// </summary>
    private static List<Entry> WithOneDefaultPerHost(List<Entry> entries)
    {
        var hostsWithDefault = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = entries.Select(e => e with { Default = e.Default && hostsWithDefault.Add(e.Host) }).ToList();
        for (var i = 0; i < result.Count; i++)
            if (hostsWithDefault.Add(result[i].Host))
                result[i] = result[i] with { Default = true };
        return result;
    }

    private static string? OptionalText(string? value, int maxLength, string what)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed.Length > maxLength || trimmed.Any(char.IsControl))
            throw new ArgumentException($"The {what} must be at most {maxLength} characters, without control characters.");
        return trimmed;
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
