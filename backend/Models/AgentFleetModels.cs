namespace AgentHub.Api.Models;

/// <summary>
/// Slim directory entry for the project agent fleet. Deliberately not a
/// <see cref="SessionInfo"/>: peers must never see each other's MCP configs,
/// repos, or runtime settings — only who exists and what they are for.
/// </summary>
public sealed record ProjectAgentInfo(
    string Id,
    string Title,
    string? Description,
    string Phase,
    SessionMode Mode,
    AgentKind Agent,
    bool QuestionPending,
    DateTime CreatedAt,
    bool Self);

/// <summary>A delivered inbox message. <c>From</c> is null for messages sent from outside a session.</summary>
public sealed record AgentMessageInfo(
    string Id,
    string? From,
    string? FromTitle,
    string Body,
    DateTime CreatedAt,
    DateTime? DeliveredAt);

/// <summary>Body of the internal send endpoint: target session id + message text.</summary>
public sealed record SendAgentMessageRequest(string? To, string? Body);

/// <summary>Body of the remote send endpoint (target session id is in the path).</summary>
public sealed record RemoteAgentMessageRequest(string? Body);

/// <summary>Shared limits and normalization for agent-to-agent messages.</summary>
public static class AgentMessaging
{
    public const int MaxBodyChars = 4000;

    /// <summary>Messages per inbox poll — small enough that a full batch stays well
    /// under the in-pod MCP client's 64 KB response cap.</summary>
    public const int InboxBatchLimit = 4;

    public const int MaxWaitSeconds = 60;

    /// <summary>Trimmed body, or null when empty or over <see cref="MaxBodyChars"/>.</summary>
    public static string? NormalizeBody(string? body)
    {
        var trimmed = body?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxBodyChars ? null : trimmed;
    }
}
