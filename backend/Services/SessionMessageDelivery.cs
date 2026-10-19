using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>Where a stored message ended up after a push was attempted (docs/priority-messages.md).</summary>
public sealed record MessageDelivery(string Via, string? Reason = null)
{
    public static readonly MessageDelivery Inbox = new(MessageDeliveryVia.Inbox);
}

/// <summary>
/// Pushes a stored message into the target session's running agent. The message is already in
/// the store when this runs; a push that fails leaves it there for the agent's inbox poll, so
/// nothing is ever lost on the way — the sender is just told it is waiting.
/// </summary>
public interface ISessionMessageDelivery
{
    Task<MessageDelivery> TryInjectAsync(SessionInfo target, SessionMessageRecord message, string? fromTitle,
        CancellationToken ct);
}

/// <summary>The one place every send endpoint goes through after storing a message, so the
/// "no delivery service, no running pod → it waits in the inbox" rule cannot drift between them.</summary>
public static class AgentMessageDispatch
{
    public static Task<MessageDelivery> PushAsync(ISessionMessageDelivery? delivery, SessionInfo? target,
        SessionMessageRecord message, string? fromTitle, CancellationToken ct)
        => delivery is null || target is null
            ? Task.FromResult(MessageDelivery.Inbox)
            : delivery.TryInjectAsync(target, message, fromTitle, ct);
}

public sealed class SessionMessageDelivery : ISessionMessageDelivery
{
    // The session agent answers once the text is in the terminal (under a second of typing
    // pauses) or queued; a wedged pod must not hold the sender's request open.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IAgentPodRequests _pods;
    private readonly ISessionStore _sessions;
    private readonly ISessionMessageStore _messages;
    private readonly ILogger<SessionMessageDelivery> _log;
    private readonly int _agentPort;

    public SessionMessageDelivery(IAgentPodRequests pods, ISessionStore sessions, ISessionMessageStore messages,
        IConfiguration configuration, ILogger<SessionMessageDelivery> log)
    {
        _pods = pods;
        _sessions = sessions;
        _messages = messages;
        _log = log;
        _agentPort = configuration.GetValue("AgentHub:AgentPort", 7681);
    }

    public async Task<MessageDelivery> TryInjectAsync(SessionInfo target, SessionMessageRecord message,
        string? fromTitle, CancellationToken ct)
    {
        if (target.Phase != "Running" || string.IsNullOrEmpty(target.PodIp))
            return new MessageDelivery(MessageDeliveryVia.Inbox, "session_not_running");

        // The callback token lives on the record, not on the public SessionInfo the callers hold.
        var record = await _sessions.GetByIdAsync(target.Id, ct);
        if (record is null) return new MessageDelivery(MessageDeliveryVia.Inbox, "session_not_found");

        var uri = new UriBuilder("http", target.PodIp, _agentPort, "agenthub/messages").Uri;
        PodDeliveryResponse? answer;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Add("X-Agent-Token", record.CallbackToken);
            request.Content = JsonContent.Create(new
            {
                id = message.Id,
                from = message.FromSessionId,
                fromTitle,
                body = message.Body,
                priority = message.Priority,
                interrupt = message.Interrupt
            });
            using var response = await _pods.SendAsync(target.Id, request, HttpCompletionOption.ResponseContentRead,
                Timeout, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Session {Id} pod refused message {Message} with HTTP {Status}",
                    target.Id, message.Id, (int)response.StatusCode);
                return new MessageDelivery(MessageDeliveryVia.Inbox, $"pod_http_{(int)response.StatusCode}");
            }
            answer = await response.Content.ReadFromJsonAsync<PodDeliveryResponse>(JsonOptions, ct);
        }
        catch (AgentPodNotConfirmedException e)
        {
            _log.LogWarning("Session {Id} pod not confirmed for message {Message}: {Reason}", target.Id, message.Id, e.Message);
            return new MessageDelivery(MessageDeliveryVia.Inbox, "pod_not_confirmed");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogWarning(e, "Session {Id} pod unreachable for message {Message}", target.Id, message.Id);
            return new MessageDelivery(MessageDeliveryVia.Inbox, "pod_unreachable");
        }

        var via = answer?.Delivered switch
        {
            "pty" or "chat" => MessageDeliveryVia.Injected,
            "queued-for-mod" => MessageDeliveryVia.Mod,
            _ => null
        };
        // The pod will hand it over when the running turn ends, by taking it from the inbox
        // itself. Marking it here would hide it from agent_inbox for as long as the turn runs,
        // and lose it if the agent exits first; left undelivered, whichever comes first takes it.
        if (answer?.Delivered == "next-turn") return new MessageDelivery(MessageDeliveryVia.NextTurn);
        if (via is null) return new MessageDelivery(MessageDeliveryVia.Inbox, answer?.Reason ?? "unavailable");

        await _messages.MarkDeliveredAsync(message.Id, via, ct);
        return new MessageDelivery(via);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>The session agent's answer: how it took the message, or why it could not.</summary>
    private sealed record PodDeliveryResponse(
        [property: JsonPropertyName("delivered")] string? Delivered,
        [property: JsonPropertyName("reason")] string? Reason);
}
