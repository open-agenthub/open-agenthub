using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>External clients act as their user, with named grants resolved on every call.</summary>
public sealed class ExternalSessionAccess(
    ISessionService sessions, ISessionAccessService? access, ISessionSharingService? sharing)
{
    public async Task<SessionInfo?> GetAsync(string owner, string id, CancellationToken ct)
    {
        // Sharing records omit owned fleet metadata; owned reads must keep the owner's record.
        if (await sessions.GetSessionAsync(owner, id, ct) is { } owned) return owned with { AccessRole = "Owner", CanWrite = true };
        var grant = access is null ? null : await access.ResolveUserAsync(owner, id, ct);
        return grant is null ? null : Project(grant);
    }

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(string owner, CancellationToken ct)
    {
        var result = (await sessions.ListSessionsAsync(owner, ct))
            .Select(s => s with { AccessRole = "Owner", CanWrite = true }).ToList();
        if (access is null || sharing is null) return result;
        IReadOnlyList<SharedSessionInfo> candidates;
        try { candidates = await sharing.ListSharedWithAsync(owner, ct); }
        catch (LicenseRequiredException) { return result; }
        var seen = result.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            var grant = await access.ResolveUserAsync(owner, candidate.Id, ct);
            if (grant is not null && seen.Add(candidate.Id)) result.Add(Project(grant));
        }
        return result;
    }

    public async Task<SessionMessageRecord> QueueSharedAsync(string owner, string id, string body,
        bool priority, bool interrupt, ISessionMessageStore messages, CancellationToken ct)
    {
        var grant = access is null ? null : await access.ResolveUserAsync(owner, id, ct);
        if (grant is null || grant.Session.Owner == owner || !SessionAccessRules.CanWriteTerminal(grant.Level))
            throw new KeyNotFoundException();
        if (priority || interrupt)
            throw new ArgumentException("Shared sessions accept normal-priority messages only.");
        var message = new SessionMessageRecord
        {
            Id = Guid.NewGuid().ToString("n")[..12], ToSessionId = id,
            Owner = grant.Session.Owner, FromOwner = owner, FromTitle = $"External client (owner: {owner})",
            Body = body
        };
        await messages.AddAsync(message, ct);
        return message;
    }

    public static SessionInfo Project(SessionAccessResult access)
    {
        var safe = SharedSessionSanitizer.Sanitize(access);
        return new SessionInfo
        {
            Id = safe.Id, Title = safe.Title, Owner = safe.Owner, Mode = safe.Mode,
            Phase = safe.Phase, UiMode = safe.UiMode, CreatedAt = safe.CreatedAt,
            Schedule = safe.Schedule, QuestionPending = safe.QuestionPending,
            ParentSessionId = safe.ParentSessionId, AccessRole = safe.AccessRole,
            CanWrite = safe.CanWrite
        };
    }
}
