// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Licensing;

namespace AgentHub.Api.Ee.Sharing;

/// <summary>Thrown by every sharing operation on an instance without an active enterprise
/// licence. HTTP surfaces answer 402, MCP tools <c>license_required</c>.</summary>
public sealed class LicenseRequiredException()
    : InvalidOperationException("An active enterprise license is required.");

/// <summary>
/// The recipient of a direct share has never signed in to this instance. A subclass of
/// <see cref="ArgumentException"/> so the existing "bad request" handling keeps working, and a
/// type of its own so a client can branch on it (<c>unknown_recipient</c>) without matching the
/// message text — rewording the sentence must not change the error code.
/// </summary>
public sealed class UnknownRecipientException(string recipient)
    : ArgumentException("Recipient is not a known user.", nameof(recipient))
{
    public string Recipient { get; } = recipient;
}

/// <summary>The owner-facing half of <see cref="SessionShareStore"/>, so the service can be
/// tested without Postgres. Access resolution stays on <see cref="ISessionAccessStore"/>.</summary>
public interface ISessionShareStore
{
    Task<SessionSharingOverview> ListForOwnerAsync(string owner, string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<StoredSessionAccess>> ListSharedWithAsync(string recipient, CancellationToken ct = default);
    Task<DirectSessionShare> UpsertDirectAsync(string owner, string sessionId, string recipient, ShareRole role, CancellationToken ct = default);
    Task DeleteDirectAsync(string owner, string sessionId, string recipient, CancellationToken ct = default);
    Task<IssuedSessionShareLink> CreateLinkAsync(string owner, string sessionId, ShareRole role, DateTime? expiresAt, CancellationToken ct = default);
    Task<SessionShareLink> UpdateLinkAsync(string owner, string sessionId, string linkId, ShareRole role, DateTime? expiresAt, CancellationToken ct = default);
    Task DeleteLinkAsync(string owner, string sessionId, string linkId, CancellationToken ct = default);
    Task<SessionMcpPolicy?> SetMcpPolicyAsync(string owner, string sessionId, IReadOnlyCollection<string>? blockedServers, IReadOnlyCollection<string>? blockedTools, CancellationToken ct = default);
}

/// <summary>
/// Everything an owner can do to a session's sharing, behind one licence check. The web
/// controller, the token API and the MCP tools all go through here; see
/// <c>docs/session-sharing-api.md</c> for why none of them talks to the store directly.
///
/// Errors: <see cref="LicenseRequiredException"/> (no licence), <see cref="KeyNotFoundException"/>
/// (the session is not the caller's, or the grant/link does not exist — one answer for both, so a
/// caller cannot probe other owners' session ids), <see cref="UnknownRecipientException"/> and
/// other <see cref="ArgumentException"/>s for invalid input.
/// </summary>
public interface ISessionSharingService
{
    Task<SessionSharingOverview> ListAsync(string owner, string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<SharedSessionInfo>> ListSharedWithAsync(string recipient, CancellationToken ct = default);
    Task<DirectSessionShare> ShareWithUserAsync(string owner, string sessionId, string recipient, ShareRole role, CancellationToken ct = default);
    Task UnshareUserAsync(string owner, string sessionId, string recipient, CancellationToken ct = default);
    /// <summary>Mints a link. The response carries the only copy of the token, as a URL a person
    /// can open — absolute when <c>FrontendOrigin</c> is configured, the path otherwise.</summary>
    Task<CreatedShareLinkResponse> CreateLinkAsync(string owner, string sessionId, ShareRole role, DateTime? expiresAt, CancellationToken ct = default);
    Task<SessionShareLink> UpdateLinkAsync(string owner, string sessionId, string linkId, ShareRole role, DateTime? expiresAt, CancellationToken ct = default);
    Task DeleteLinkAsync(string owner, string sessionId, string linkId, CancellationToken ct = default);
    Task<SessionMcpPolicy?> SetMcpPolicyAsync(string owner, string sessionId, IReadOnlyCollection<string>? blockedServers, IReadOnlyCollection<string>? blockedTools, CancellationToken ct = default);
}

public static class ShareLinkUrl
{
    /// <summary>
    /// Where a share link opens. Built from <c>FrontendOrigin</c> for the same reason as
    /// <see cref="Services.SessionUrl"/>: the URL is handed to a person, and a forwarded Host
    /// header must not decide where they end up. Unlike <c>SessionInfo.Url</c> this never yields
    /// null — the token is shown exactly once, and a null here would throw it away — so without
    /// an origin the path alone is returned, which still resolves inside the web app.
    /// </summary>
    public static string For(string? frontendOrigin, string token)
    {
        var path = "/shared/" + Uri.EscapeDataString(token);
        var origin = frontendOrigin?.Trim().TrimEnd('/');
        return string.IsNullOrEmpty(origin) ? path : origin + path;
    }
}

public sealed class SessionSharingService(
    ISessionShareStore store,
    IEnterpriseLicense license,
    string? frontendOrigin) : ISessionSharingService
{
    public Task<SessionSharingOverview> ListAsync(string owner, string sessionId, CancellationToken ct = default)
    {
        RequireLicense();
        return store.ListForOwnerAsync(owner, sessionId, ct);
    }

    public async Task<IReadOnlyList<SharedSessionInfo>> ListSharedWithAsync(string recipient, CancellationToken ct = default)
    {
        RequireLicense();
        var stored = await store.ListSharedWithAsync(recipient, ct);
        var result = new List<SharedSessionInfo>(stored.Count);
        foreach (var access in stored)
        {
            // The query already excludes the recipient's own sessions, so "owns" is false here;
            // the level comes from the stored role alone, exactly as SessionAccessService resolves
            // a single session.
            var level = SessionAccessRules.Resolve(owns: false, access.Role);
            if (level == SessionAccessLevel.None) continue;
            result.Add(SharedSessionSanitizer.Sanitize(
                new SessionAccessResult(access.Session, level, access.Session.Owner)));
        }
        return result;
    }

    public Task<DirectSessionShare> ShareWithUserAsync(string owner, string sessionId, string recipient, ShareRole role, CancellationToken ct = default)
    {
        RequireLicense();
        return store.UpsertDirectAsync(owner, sessionId, recipient, role, ct);
    }

    public Task UnshareUserAsync(string owner, string sessionId, string recipient, CancellationToken ct = default)
    {
        RequireLicense();
        return store.DeleteDirectAsync(owner, sessionId, recipient, ct);
    }

    public async Task<CreatedShareLinkResponse> CreateLinkAsync(string owner, string sessionId, ShareRole role, DateTime? expiresAt, CancellationToken ct = default)
    {
        RequireLicense();
        var issued = await store.CreateLinkAsync(owner, sessionId, role, expiresAt, ct);
        return new CreatedShareLinkResponse(issued.Link, ShareLinkUrl.For(frontendOrigin, issued.Token));
    }

    public Task<SessionShareLink> UpdateLinkAsync(string owner, string sessionId, string linkId, ShareRole role, DateTime? expiresAt, CancellationToken ct = default)
    {
        RequireLicense();
        return store.UpdateLinkAsync(owner, sessionId, linkId, role, expiresAt, ct);
    }

    public Task DeleteLinkAsync(string owner, string sessionId, string linkId, CancellationToken ct = default)
    {
        RequireLicense();
        return store.DeleteLinkAsync(owner, sessionId, linkId, ct);
    }

    public Task<SessionMcpPolicy?> SetMcpPolicyAsync(string owner, string sessionId, IReadOnlyCollection<string>? blockedServers, IReadOnlyCollection<string>? blockedTools, CancellationToken ct = default)
    {
        RequireLicense();
        return store.SetMcpPolicyAsync(owner, sessionId, blockedServers, blockedTools, ct);
    }

    /// <summary>Checked before the store is touched, so a Community instance never opens a
    /// connection for a call it is going to refuse.</summary>
    private void RequireLicense()
    {
        if (!license.Enabled) throw new LicenseRequiredException();
    }
}
