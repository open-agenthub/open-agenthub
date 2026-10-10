namespace AgentHub.Api.Models;

/// <summary>One account as <see cref="AccountStatus"/> lists it: enough to pick one, never a secret.</summary>
public sealed record AccountStatusEntry(string Id, string Label, string? Email, string? Organization,
    bool IsDefault, bool IsExhausted, DateTime? ExhaustedUntil)
{
    public static AccountStatusEntry From(ProviderAccountInfo info) =>
        new(info.Id, info.Label, info.Email, info.Organization, info.IsDefault, info.IsExhausted, info.ExhaustedUntil);
}

/// <summary>
/// What <c>account_status</c> answers (docs/account-limits.md): the account a session runs on,
/// whether it is at its usage limit, the session's failover setting and the owner's other
/// accounts of the same provider. Built from the session and the accounts listing, so the
/// remote MCP, the internal route and the REST route cannot drift in what they say.
/// </summary>
public sealed record AccountStatus(
    string SessionId,
    AgentKind Agent,
    AgentAuthMode AuthMode,
    string Phase,
    /// <summary>The pinned account; null when the session follows the default.</summary>
    string? CredentialId,
    /// <summary>The account the last start mounted; null before the first start.</summary>
    string? ResolvedCredentialId,
    /// <summary>The account the session runs on — pinned, else resolved, else the default — or
    /// null for an API-key session or an owner with no login stored.</summary>
    AccountStatusEntry? Account,
    string AccountFailover,
    /// <summary>The owner's other accounts of this provider, exhausted ones included and marked.</summary>
    IReadOnlyList<AccountStatusEntry> Alternatives)
{
    public static AccountStatus From(SessionInfo session,
        IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>> accounts)
    {
        var stored = accounts.TryGetValue(session.Agent.ToString(), out var list) ? list : Array.Empty<ProviderAccountInfo>();
        ProviderAccountInfo? current = null;
        if (session.AuthMode != AgentAuthMode.ApiKey)
        {
            current = stored.FirstOrDefault(a => a.Id == session.CredentialId)
                ?? stored.FirstOrDefault(a => a.Id == session.ResolvedCredentialId)
                ?? stored.FirstOrDefault(a => a.IsDefault)
                ?? stored.FirstOrDefault();
        }
        var alternatives = stored.Where(a => a.Id != current?.Id).Select(AccountStatusEntry.From).ToList();
        return new AccountStatus(session.Id, session.Agent, session.AuthMode, session.Phase,
            session.CredentialId, session.ResolvedCredentialId,
            current is null ? null : AccountStatusEntry.From(current),
            session.AccountFailover, alternatives);
    }
}
