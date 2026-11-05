using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

namespace AgentHub.Api.Ee.Sharing;

public sealed record ShareLinkReadResult(SharedSessionInfo Session, ConversationPage History);

/// <summary>Explicit bearer-link authority; it never grants the holder named user access.</summary>
public sealed class ExternalShareLinkService(
    ISessionAccessService access, ISharedSessionHistory history, ISessionMessageStore messages)
{
    public async Task<ShareLinkReadResult?> ReadAsync(string link, int? offset, int? max, CancellationToken ct)
    {
        var token = Token(link);
        var resolved = token is null ? null : await access.ResolveTokenReadOnlyAsync(token, ct);
        if (resolved is null) return null;
        return new(SharedSessionSanitizer.Sanitize(resolved), await history.ReadAsync(resolved, offset, max, ct));
    }

    public async Task<AgentMessageSendResult?> SendAsync(string link, string body, CancellationToken ct)
    {
        var token = Token(link);
        var resolved = token is null ? null : await access.ResolveTokenAsync(token, ct);
        if (resolved is null || !SessionAccessRules.CanWriteTerminal(resolved.Level)) return null;
        var text = AgentMessaging.NormalizeBody(body)
            ?? throw new ArgumentException("message_body_invalid");
        // A link is an anonymous capability, not evidence of the holder's account identity.
        var message = new SessionMessageRecord
        {
            Id = Guid.NewGuid().ToString("n")[..12], ToSessionId = resolved.Session.Id,
            Owner = resolved.Session.Owner, FromOwner = "via share link", FromTitle = "via share link",
            Body = text
        };
        await messages.AddAsync(message, ct);
        return new(message.Id, message.ToSessionId, MessageDeliveryVia.Inbox, null);
    }

    private static string? Token(string? value)
    {
        var candidate = value?.Trim();
        if (candidate is null) return null;
        if (!candidate.StartsWith("/shared/", StringComparison.Ordinal)
            && Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is not ("https" or "http")) return null;
            candidate = uri.AbsolutePath;
        }
        if (candidate.StartsWith("/shared/", StringComparison.Ordinal)) candidate = candidate[8..];
        return ShareTokens.TryHash(candidate, out _) ? candidate : null;
    }
}
