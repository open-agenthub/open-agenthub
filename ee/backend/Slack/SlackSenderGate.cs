// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Collections.Concurrent;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Ee.Slack;

/// <summary>The workspace-verified email of a Slack user; <see cref="SlackClient"/> answers it from users.info.</summary>
public interface ISlackUserEmails
{
    Task<string?> GetUserEmailAsync(string userId, CancellationToken ct);
}

public enum SlackSenderVerdict { Allowed, Unlinked, Refused }

/// <summary>Whether a Slack sender may drive a session, and what to tell them if not.</summary>
/// <param name="Notice">What to tell the sender, or null when they were told already.</param>
public sealed record SlackSenderDecision(SlackSenderVerdict Verdict, string? Principal, string? Notice)
{
    public bool Allowed => Verdict == SlackSenderVerdict.Allowed;
}

/// <summary>
/// Decides whether a Slack user may drive a session from its thread: type into it, ask
/// "!status", or answer its permission prompts. A session thread can sit in a channel
/// (channel override, the instance fallback channel, "!new" in a channel), and everyone in
/// that channel can post in the thread and click its buttons — so the thread itself is no
/// proof of who is speaking. The Slack user is mapped to an AgentHub account the same way
/// "!new" does (workspace-verified email, exactly one match), and that account then needs
/// write access under <see cref="SessionAccessRules"/>: the owner, or a Collaborator.
/// </summary>
public sealed class SlackSenderGate(ISlackUserEmails slack, UserDirectory users, ISessionAccessService access)
{
    public const string UnlinkedNotice =
        ":lock: Only the session's owner can reply here or answer its prompts. Your Slack account is not linked to an " +
        "AgentHub account: its Slack email has to match the email of exactly one AgentHub user.";

    public const string RefusedNotice =
        ":lock: Only the session's owner (or a Collaborator it is shared with) can reply here or answer its prompts.";

    // Telling a sender once is enough; a refusal on every message would turn the thread into
    // noise for its owner. In memory only: after a restart, or on another replica, a sender is
    // told once more, which is harmless.
    private const int MaxRemembered = 10_000;
    private readonly ConcurrentDictionary<string, byte> _told = new(StringComparer.Ordinal);

    public async Task<SlackSenderDecision> CheckAsync(string? slackUserId, string sessionOwner, string sessionId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(slackUserId))
            return new(SlackSenderVerdict.Unlinked, null, null);

        // SlackEnabled is deliberately not required, unlike for "!new": it is the owner's
        // consent to be notified, not part of who they are. Requiring it would lock an owner
        // out of a thread they already have after they switch notifications off.
        var email = await slack.GetUserEmailAsync(slackUserId, ct);
        var user = string.IsNullOrWhiteSpace(email) ? null : await users.GetUniqueByEmailAsync(email, ct);
        if (user is null)
            return new(SlackSenderVerdict.Unlinked, null, Once($"unlinked:{slackUserId}", UnlinkedNotice));

        // The owner is decided here and not through the share store, so a same-owner reply
        // never depends on a sharing lookup or on the licence state of sharing.
        if (string.Equals(user.Owner, sessionOwner, StringComparison.Ordinal))
            return new(SlackSenderVerdict.Allowed, user.Owner, null);

        var resolved = await access.ResolveUserAsync(user.Owner, sessionId, ct);
        // ResolveUserAsync looks the session up by id alone; a thread whose stored owner
        // disagrees with the session record must not let a grant on some other owner's
        // session of the same id through.
        if (resolved is not null && string.Equals(resolved.Session.Owner, sessionOwner, StringComparison.Ordinal) &&
            SessionAccessRules.CanWriteTerminal(resolved.Level))
            return new(SlackSenderVerdict.Allowed, user.Owner, null);

        return new(SlackSenderVerdict.Refused, user.Owner, Once($"refused:{slackUserId}:{sessionId}", RefusedNotice));
    }

    private string? Once(string key, string notice)
    {
        if (_told.Count >= MaxRemembered) _told.Clear();
        return _told.TryAdd(key, 0) ? notice : null;
    }
}
