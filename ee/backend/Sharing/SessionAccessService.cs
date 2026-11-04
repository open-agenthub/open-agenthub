// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Licensing;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Ee.Sharing;

/// <summary>A session and every grant the store found for one principal on it — its own share or
/// link and, for a principal, the direct shares up its parentSessionId chain. Which grant wins is
/// decided by <see cref="SessionAccessRules"/>, not here.</summary>
public sealed record StoredSessionAccess(SessionRecord Session, IReadOnlyList<ShareGrant> Grants)
{
    /// <summary>A session with at most one grant, on the session itself.</summary>
    public StoredSessionAccess(SessionRecord session, ShareRole? role)
        : this(session, role is { } value
            ? [new ShareGrant(value, ShareGrantScope.Session, session.Id)]
            : Array.Empty<ShareGrant>())
    {
    }

    /// <summary>The grant on the session itself, ignoring inherited ones; not the effective role.</summary>
    public ShareRole? Role => Grants.FirstOrDefault(grant => grant.Scope == ShareGrantScope.Session)?.Role;
}

public interface ISessionAccessStore
{
    Task<StoredSessionAccess?> FindUserAccessAsync(
        string principal,
        string sessionId,
        CancellationToken ct = default);

    Task<StoredSessionAccess?> FindTokenAccessAsync(
        string token,
        CancellationToken ct = default);

    Task<StoredSessionAccess?> FindTokenAccessReadOnlyAsync(
        string token,
        CancellationToken ct = default) => FindTokenAccessAsync(token, ct);
}

public interface ISessionAccessService
{
    Task<SessionAccessResult?> ResolveUserAsync(
        string principal,
        string sessionId,
        CancellationToken ct = default);

    Task<SessionAccessResult?> ResolveTokenAsync(
        string token,
        CancellationToken ct = default);

    Task<SessionAccessResult?> ResolveTokenReadOnlyAsync(
        string token,
        CancellationToken ct = default) => ResolveTokenAsync(token, ct);
}

public sealed class SessionAccessService(ISessionAccessStore store, IEnterpriseLicense license)
    : ISessionAccessService
{
    public async Task<SessionAccessResult?> ResolveUserAsync(
        string principal,
        string sessionId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(principal) || string.IsNullOrWhiteSpace(sessionId))
            return null;

        var stored = await store.FindUserAccessAsync(principal, sessionId, ct);
        if (stored is null)
            return null;

        var owns = string.Equals(stored.Session.Owner, principal, StringComparison.Ordinal);
        if (owns)
            return new SessionAccessResult(stored.Session, SessionAccessLevel.Owner, null);
        if (!license.Enabled)
            return null;

        return SessionAccessRules.ForRecipient(stored.Session, stored.Grants);
    }

    public async Task<SessionAccessResult?> ResolveTokenAsync(
        string token,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        if (!license.Enabled)
            return null;

        var stored = await store.FindTokenAccessAsync(token, ct);
        return stored is null ? null : SessionAccessRules.ForRecipient(stored.Session, stored.Grants);
    }

    public async Task<SessionAccessResult?> ResolveTokenReadOnlyAsync(
        string token,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || !license.Enabled)
            return null;
        var stored = await store.FindTokenAccessReadOnlyAsync(token, ct);
        return stored is null ? null : SessionAccessRules.ForRecipient(stored.Session, stored.Grants);
    }
}
