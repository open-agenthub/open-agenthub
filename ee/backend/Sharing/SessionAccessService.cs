using AgentHub.Api.Persistence;

namespace AgentHub.Api.Ee.Sharing;

public sealed record StoredSessionAccess(SessionRecord Session, ShareRole? Role);

public interface ISessionAccessStore
{
    Task<StoredSessionAccess?> FindUserAccessAsync(
        string principal,
        string sessionId,
        CancellationToken ct = default);

    Task<StoredSessionAccess?> FindTokenAccessAsync(
        string token,
        CancellationToken ct = default);
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
}

public sealed class SessionAccessService(ISessionAccessStore store) : ISessionAccessService
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
        var level = SessionAccessRules.Resolve(owns, stored.Role);
        return level == SessionAccessLevel.None
            ? null
            : new SessionAccessResult(stored.Session, level, owns ? null : stored.Session.Owner);
    }

    public async Task<SessionAccessResult?> ResolveTokenAsync(
        string token,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var stored = await store.FindTokenAccessAsync(token, ct);
        if (stored is null)
            return null;

        var level = SessionAccessRules.Resolve(false, stored.Role);
        return level == SessionAccessLevel.None
            ? null
            : new SessionAccessResult(stored.Session, level, stored.Session.Owner);
    }
}
