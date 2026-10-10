using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>A create request asked for a credential its token may not hand out. The code is the
/// body of the 403 so an MCP client has a stable word to act on.</summary>
public sealed class CredentialScopeException(string code) : Exception(code)
{
    public string Code { get; } = code;

    public const string CredentialNotAllowed = "credential_not_allowed";
    public const string CredentialRequired = "credential_required";
    public const string AgentNotAllowed = "agent_not_allowed";
    public const string ApiKeysNotAllowed = "api_keys_not_allowed";
    public const string GitPatNotAllowed = "git_pat_not_allowed";
}

/// <summary>
/// Applies a token's scope to what reaches the session service and to what a listing shows
/// (docs/credential-scopes.md). Pure functions over the owner's current accounts and PATs, so the
/// matrix is tested without a cluster. The session service keeps validating ids against the store;
/// the scope only narrows the request before it gets there.
/// </summary>
public static class CredentialScope
{
    public static CreateSessionRequest ApplyToCreate(CreateSessionRequest req, ApiTokenScope? scope,
        IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> accounts, IReadOnlyList<GitPatInfo> gitPats)
    {
        if (scope is null) return req;
        req = ApplyProvider(req, scope, accounts);
        return ApplyGit(req, scope, gitPats);
    }

    private static CreateSessionRequest ApplyProvider(CreateSessionRequest req, ApiTokenScope scope,
        IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> accounts)
    {
        if (req.AuthMode == AgentAuthMode.ApiKey)
        {
            if (!scope.AllowsApiKeys) throw new CredentialScopeException(CredentialScopeException.ApiKeysNotAllowed);
            return req;
        }

        var allowed = scope.AccountsFor(req.Agent)
            ?? throw new CredentialScopeException(CredentialScopeException.AgentNotAllowed);
        var requested = string.IsNullOrWhiteSpace(req.CredentialId) ? null : req.CredentialId.Trim();
        if (requested is not null)
        {
            if (!scope.AllowsAccount(req.Agent, requested))
                throw new CredentialScopeException(CredentialScopeException.CredentialNotAllowed);
            return req;
        }
        if (allowed.Contains(ApiTokenScope.Any)) return req;

        var stored = accounts.TryGetValue(req.Agent.ToString(), out var list) ? list : Array.Empty<ProviderAccountInfo>();
        // The default is what a request without credentialId would get; when the token may use
        // it, nothing has to change and the session keeps following the default.
        var fallback = stored.FirstOrDefault(a => a.IsDefault) ?? stored.FirstOrDefault();
        if (fallback is not null && allowed.Contains(fallback.Id)) return req;

        // A token restricted to one account should not have to repeat that id in every call; two
        // allowed accounts would be a guess, and work started on the wrong login is worse than a
        // refusal.
        var candidates = stored.Where(a => allowed.Contains(a.Id)).ToList();
        if (candidates.Count == 1) return req with { CredentialId = candidates[0].Id };
        throw new CredentialScopeException(CredentialScopeException.CredentialRequired);
    }

    private static CreateSessionRequest ApplyGit(CreateSessionRequest req, ApiTokenScope scope, IReadOnlyList<GitPatInfo> gitPats)
    {
        if (scope.AllowsAllGitPats) return req;
        var allowedStored = gitPats.Where(p => scope.AllowsGitPat(p.Id)).Select(p => p.Id).ToList();
        if (req.GitPatIds is null) return req with { GitPatIds = allowedStored };

        var ids = req.GitPatIds.Select(id => id?.Trim() ?? "").Where(id => id.Length > 0).ToList();
        // "all" from a restricted token is the allowed set, not the owner's whole list.
        if (ids.Contains(GitPatSelection.All)) return req with { GitPatIds = allowedStored };
        if (ids.Any(id => !scope.AllowsGitPat(id)))
            throw new CredentialScopeException(CredentialScopeException.GitPatNotAllowed);
        return req with { GitPatIds = ids };
    }

    /// <summary>The listing narrowed to what the token may use: a leaked restricted token tells its
    /// holder nothing about logins it cannot use anyway.</summary>
    public static RemoteCredentialListing FilterListing(RemoteCredentialListing listing, ApiTokenScope? scope)
    {
        if (scope is null) return listing;
        var accounts = new Dictionary<string, IReadOnlyList<ProviderAccountInfo>>();
        foreach (var (name, list) in listing.Accounts)
        {
            accounts[name] = Enum.TryParse<AgentKind>(name, ignoreCase: true, out var agent)
                ? list.Where(a => scope.AllowsAccount(agent, a.Id)).ToList()
                : Array.Empty<ProviderAccountInfo>();
        }
        var pats = listing.GitPats.Where(p => scope.AllowsGitPat(p.Id)).ToList();
        var keys = scope.AllowsApiKeys ? listing.ApiKeys : new RemoteCredentialApiKeys(false, false, false);
        return new RemoteCredentialListing(accounts, pats, keys);
    }
}
