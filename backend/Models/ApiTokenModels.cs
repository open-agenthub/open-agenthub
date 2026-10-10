using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentHub.Api.Models;

/// <summary>
/// Which credentials a session created with a personal API token may use
/// (docs/credential-scopes.md). Null on a token means "everything", which is what every token
/// did before scopes existed. A scope is an allow list in every part: a provider that is not
/// named may not be used, a missing <see cref="GitPats"/> means no PAT, a missing
/// <see cref="ApiKeys"/> means no API-key session. The wildcard <see cref="Any"/> allows every
/// account of a provider, or every PAT, including ones stored later.
/// </summary>
public sealed record ApiTokenScope
{
    public const string Any = "*";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Agent name (<c>Claude</c>, <c>Codex</c>, …) to allowed account ids, or <c>["*"]</c>.</summary>
    public Dictionary<string, List<string>>? ProviderAccounts { get; init; }
    /// <summary>Allowed git PAT ids, or <c>["*"]</c>; null = none.</summary>
    public List<string>? GitPats { get; init; }
    /// <summary>Whether an API-key session may be created; null = no.</summary>
    public bool? ApiKeys { get; init; }

    public bool AllowsApiKeys => ApiKeys == true;

    /// <summary>The allowed ids for an agent — <c>["*"]</c> for any — or null when the agent is not allowed at all.</summary>
    public IReadOnlyList<string>? AccountsFor(AgentKind agent)
    {
        if (ProviderAccounts is null) return null;
        var entry = ProviderAccounts.FirstOrDefault(p => string.Equals(p.Key, agent.ToString(), StringComparison.OrdinalIgnoreCase));
        return entry.Key is null ? null : entry.Value;
    }

    public bool AllowsAnyAccountOf(AgentKind agent) => AccountsFor(agent)?.Contains(Any) == true;

    public bool AllowsAccount(AgentKind agent, string id) =>
        AccountsFor(agent) is { } allowed && (allowed.Contains(Any) || allowed.Contains(id));

    public bool AllowsAllGitPats => GitPats?.Contains(Any) == true;

    public bool AllowsGitPat(string id) => AllowsAllGitPats || GitPats?.Contains(id) == true;

    /// <summary>
    /// Checks a scope a user is storing against what they have right now: every id must name a
    /// stored account or PAT, agent names are matched case-insensitively and stored canonically.
    /// An unknown id is refused here, while the person is looking at the form, rather than kept
    /// as a restriction that silently allows nothing because it points at a removed login.
    /// </summary>
    public static ApiTokenScope Normalize(ApiTokenScope scope,
        IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> accounts, IReadOnlyList<GitPatInfo> gitPats)
    {
        Dictionary<string, List<string>>? providers = null;
        if (scope.ProviderAccounts is not null)
        {
            providers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (name, ids) in scope.ProviderAccounts)
            {
                if (!Enum.TryParse<AgentKind>(name, ignoreCase: true, out var agent) || !Enum.IsDefined(agent))
                    throw new ArgumentException($"Unknown agent '{name}' in allowedCredentials.providerAccounts.");
                var key = agent.ToString();
                var stored = accounts.TryGetValue(key, out var list) ? list : Array.Empty<ProviderAccountInfo>();
                var cleaned = CleanIds(ids, id => stored.Any(a => a.Id == id),
                    id => $"No stored {key} login with id '{id}'.");
                if (providers.TryGetValue(key, out var existing)) existing.AddRange(cleaned.Where(id => !existing.Contains(id)));
                else providers[key] = cleaned;
            }
        }

        var pats = scope.GitPats is null
            ? null
            : CleanIds(scope.GitPats, id => gitPats.Any(p => p.Id == id), id => $"No stored git token with id '{id}'.");

        return new ApiTokenScope { ProviderAccounts = providers, GitPats = pats, ApiKeys = scope.ApiKeys == true ? true : null };
    }

    private static List<string> CleanIds(IEnumerable<string> ids, Func<string, bool> exists, Func<string, string> missing)
    {
        var result = new List<string>();
        foreach (var raw in ids)
        {
            var id = raw?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            if (id == Any) return [Any];
            if (!exists(id)) throw new ArgumentException(missing(id));
            if (!result.Contains(id)) result.Add(id);
        }
        return result;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a stored scope. Malformed JSON is treated as the most restrictive scope rather
    /// than as "unrestricted": a column that cannot be read must not widen what a token can do.</summary>
    public static ApiTokenScope? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ApiTokenScope>(json, Json) ?? new ApiTokenScope(); }
        catch (JsonException) { return new ApiTokenScope(); }
    }
}

/// <summary>Who a personal API token resolves to, and what it may hand a session.</summary>
public sealed record RemoteCaller(string Owner, ApiTokenScope? Scope);
