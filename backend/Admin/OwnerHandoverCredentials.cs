using AgentHub.Api.Models;
using AgentHub.Api.Services;

namespace AgentHub.Api.Admin;

/// <summary>
/// One kind of stored credential in a handover preview. <paramref name="Count"/> is what the source
/// holds; <paramref name="Kept"/> how many of those stay behind because the target already has its
/// own (a single slot such as an SSH key, a Git OAuth provider) or has no room left (user secrets).
/// Never a value: the preview is the only place an admin sees these, and a name is all they need.
/// </summary>
public sealed record CredentialKindPreview(string Kind, int Count, int Kept);

/// <summary>Merged secret data plus what the merge did, per kind.</summary>
public sealed record CredentialMerge(Dictionary<string, byte[]> Data, IReadOnlyList<CredentialKindPreview> Kinds);

/// <summary>
/// How the source's stored credentials join the target's secrets in an owner handover
/// (docs/apikey-handover.md). Pure functions over secret data, so the preview and the copy run the
/// same code and the rules are tested without a cluster.
///
/// The target's own credentials always win: a single slot it already filled keeps its value, an
/// account or token whose id it already has is not touched, and its defaults stay its defaults.
/// Taking the source's instead would silently swap the subscription or Git identity the target's
/// own sessions run on. Everything merges by id, so a rerun adds nothing twice.
/// </summary>
public static class OwnerHandoverCredentials
{
    public const string ApiKeys = "apiKeys";
    public const string SshKey = "sshKey";
    public const string GitIdentity = "gitIdentity";
    public const string GitPats = "gitPats";
    public const string GitOAuth = "gitOAuth";
    public const string UserSecrets = "userSecrets";
    public static string ProviderLogins(AgentKind agent) => $"providerLogins.{agent.ToString().ToLowerInvariant()}";

    private static readonly (string Key, string Kind)[] SingleSlots =
    [
        ("anthropic_api_key", ApiKeys), ("openai_api_key", ApiKeys), ("cursor_api_key", ApiKeys),
        ("opencode_api_key", ApiKeys), ("custom_endpoint_api_key", ApiKeys),
        ("ssh_key", SshKey),
        ("known_hosts", GitIdentity), ("git_user_name", GitIdentity), ("git_user_email", GitIdentity),
    ];

    /// <summary>The general credential secret (<c>creds-&lt;owner&gt;</c>): single slots and the PAT list.</summary>
    public static CredentialMerge MergeGeneral(IDictionary<string, byte[]>? target, IDictionary<string, byte[]>? source)
    {
        var data = Copy(target);
        var counts = new Dictionary<string, (int Count, int Kept)>();
        void Note(string kind, bool kept)
        {
            var c = counts.GetValueOrDefault(kind);
            counts[kind] = (c.Count + 1, c.Kept + (kept ? 1 : 0));
        }

        foreach (var (key, kind) in SingleSlots)
        {
            if (source is null || !source.TryGetValue(key, out var value)) continue;
            if (data.TryGetValue(key, out var own) && !own.AsSpan().SequenceEqual(value)) { Note(kind, kept: true); continue; }
            data[key] = value;
            Note(kind, kept: false);
        }

        var pats = GitPatStore.Read(data).ToList();
        var sourcePats = GitPatStore.Read(source);
        foreach (var pat in sourcePats)
        {
            Note(GitPats, kept: false);
            if (pats.Any(p => p.Id == pat.Id)) continue;
            // The target's default for a host stays its default; an added token for a host it has
            // no token for becomes that host's default, since it is then the only one.
            var hostCovered = pats.Any(p => string.Equals(p.Host, pat.Host, StringComparison.OrdinalIgnoreCase));
            pats.Add(pat with { Default = pat.Default && !hostCovered });
        }
        if (sourcePats.Count > 0 || GitPatStore.HasLegacySlots(data)) GitPatStore.Write(data, pats);
        return new CredentialMerge(data, counts.Select(kv => new CredentialKindPreview(kv.Key, kv.Value.Count, kv.Value.Kept)).ToList());
    }

    /// <summary>A provider login secret: accounts merge by id; the target's default stays.</summary>
    public static CredentialMerge MergeProvider(IDictionary<string, byte[]>? target, IDictionary<string, byte[]>? source, AgentKind agent)
    {
        var mine = ProviderAccountSecret.Read(target, agent);
        var theirs = ProviderAccountSecret.Read(source, agent);
        if (theirs.Accounts.Count == 0) return new CredentialMerge(Copy(target), []);
        var hadAny = mine.Accounts.Count > 0;
        foreach (var account in theirs.Accounts)
        {
            if (mine.Find(account.Id) is not null) continue;
            mine.Accounts.Add(new ProviderAccount
            {
                Id = account.Id, Label = account.Label, Identity = account.Identity, CreatedAt = account.CreatedAt,
                LastUsedAt = account.LastUsedAt, ExhaustedUntil = account.ExhaustedUntil,
                ExhaustedReason = account.ExhaustedReason, IsDefault = account.IsDefault && !hadAny,
            });
            mine.Files[account.Id] = theirs.Files[account.Id];
        }
        return new CredentialMerge(ProviderAccountSecret.Write(mine, agent),
            [new CredentialKindPreview(ProviderLogins(agent), theirs.Accounts.Count, 0)]);
    }

    /// <summary>The user's secret store (<c>usersecrets-&lt;owner&gt;</c>): entries by id, up to its limit.</summary>
    public static CredentialMerge MergeUserSecrets(IDictionary<string, byte[]>? target, IDictionary<string, byte[]>? source)
    {
        var data = Copy(target);
        var (moved, skipped) = SessionSecretStore.MergeFrom(data, source);
        return new CredentialMerge(data, moved + skipped == 0 ? [] : [new CredentialKindPreview(UserSecrets, moved + skipped, skipped)]);
    }

    /// <summary>
    /// Connected Git providers (<c>gitauth-&lt;owner&gt;</c>), keyed <c>&lt;providerId&gt;.*</c>. A provider
    /// moves as a whole or not at all: mixing one grant's access token with another's refresh
    /// token would break the next renewal.
    /// </summary>
    public static CredentialMerge MergeGitOAuth(IDictionary<string, byte[]>? target, IDictionary<string, byte[]>? source)
    {
        var data = Copy(target);
        if (source is null || source.Count == 0) return new CredentialMerge(data, []);
        static string Provider(string key) => key.Split('.', 2)[0];
        var own = data.Keys.Select(Provider).ToHashSet(StringComparer.Ordinal);
        var groups = source.GroupBy(kv => Provider(kv.Key)).ToList();
        var kept = 0;
        foreach (var group in groups)
        {
            if (own.Contains(group.Key))
            {
                // Identical content is the rerun after a completed copy, not a clash.
                if (group.All(kv => data.TryGetValue(kv.Key, out var v) && v.AsSpan().SequenceEqual(kv.Value))) continue;
                kept++;
                continue;
            }
            foreach (var (key, value) in group) data[key] = value;
        }
        return new CredentialMerge(data, [new CredentialKindPreview(GitOAuth, groups.Count, kept)]);
    }

    private static Dictionary<string, byte[]> Copy(IDictionary<string, byte[]>? data) =>
        data is null ? new Dictionary<string, byte[]>(StringComparer.Ordinal) : new Dictionary<string, byte[]>(data, StringComparer.Ordinal);

    /// <summary>Adds up the per-secret results by kind.</summary>
    public static IReadOnlyList<CredentialKindPreview> Sum(IEnumerable<CredentialKindPreview> kinds) =>
        kinds.GroupBy(k => k.Kind)
            .Select(g => new CredentialKindPreview(g.Key, g.Sum(k => k.Count), g.Sum(k => k.Kept)))
            .Where(k => k.Count > 0)
            .OrderBy(k => k.Kind, StringComparer.Ordinal)
            .ToList();
}
