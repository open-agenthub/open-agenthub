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

/// <summary>
/// A delivered inbox message. <c>From</c> is null for messages sent from outside a session.
/// <c>DeliveredVia</c> says how it reached the agent (<see cref="MessageDeliveryVia"/>);
/// null while it is still waiting in the inbox.
/// </summary>
public sealed record AgentMessageInfo(
    string Id,
    string? From,
    string? FromTitle,
    string Body,
    DateTime CreatedAt,
    DateTime? DeliveredAt,
    bool Priority = false,
    bool Interrupt = false,
    string? DeliveredVia = null);

/// <summary>Body of the internal send endpoint: target session id + message text. A priority
/// message is pushed into the target's running agent instead of waiting in its inbox; interrupt
/// additionally stops the agent's current work first and implies priority.</summary>
public sealed record SendAgentMessageRequest(string? To, string? Body, bool? Priority = null, bool? Interrupt = null);

/// <summary>Body of the remote and owner send endpoints (target session id is in the path).</summary>
public sealed record RemoteAgentMessageRequest(string? Body, bool? Priority = null, bool? Interrupt = null);

/// <summary>What the sender is told about where the message went.</summary>
public sealed record AgentMessageSendResult(string Id, string To, string DeliveredVia, string? Reason = null);

/// <summary>The ways a message reaches its agent, as reported back to the sender and stored in
/// <c>session_messages.delivered_via</c>.</summary>
public static class MessageDeliveryVia
{
    /// <summary>Waiting in, or taken from, the pull inbox (<c>agent_inbox</c>).</summary>
    public const string Inbox = "inbox";
    /// <summary>Written into the agent's terminal or chat pipe by the session agent.</summary>
    public const string Injected = "injected";
    /// <summary>Handed to the Claude Code mod running inside the agent process.</summary>
    public const string Mod = "mod";
}

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

    /// <summary>Resolves the two flags: interrupt implies priority, so a sender cannot ask for an
    /// interruption that then sits in the inbox.</summary>
    public static (bool Priority, bool Interrupt) ResolveFlags(bool? priority, bool? interrupt)
    {
        var stop = interrupt == true;
        return (stop || priority == true, stop);
    }
}
