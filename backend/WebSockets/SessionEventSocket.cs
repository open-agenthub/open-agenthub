using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Events;

namespace AgentHub.Api.WebSockets;

/// <summary>
/// Server push for "something about this session changed". Replaces a one-and-a-half second
/// poll per open workspace; the client answers an event by re-reading the REST endpoint it
/// would otherwise have been calling on a timer.
/// </summary>
public static class SessionEventSocket
{
    // The socket says what changed, never what it changed to. That keeps one authorization
    // decision in one place — the REST read the client follows up with — instead of teaching
    // this path which fields a shared-link viewer is allowed to see.
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    // A browser view relays live content and re-checks every two seconds; this socket carries
    // no content, so a revoked viewer who holds it open learns only that an event occurred, and
    // their follow-up read is refused. Checking less often costs a query per socket per minute
    // instead of thirty.
    private static readonly TimeSpan AuthorizationInterval = TimeSpan.FromSeconds(30);

    public static async Task HandleAsync(
        HttpContext context,
        string sessionId,
        ISessionEventBus events,
        ILoggerFactory loggerFactory,
        Func<CancellationToken, Task<bool>>? remainsAuthorized = null)
    {
        var log = loggerFactory.CreateLogger("SessionEventSocket");

        // Subscribed before the handshake on purpose: a change committed between accepting the
        // socket and attaching the listener would otherwise fall into the gap, and the client
        // would sit on a stale list with no poll left to rescue it.
        using var subscription = events.Subscribe(sessionId);
        using var client = await context.WebSockets.AcceptWebSocketAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

        var pushing = PushAsync(client, subscription, cts.Token);
        var draining = DrainAsync(client, cts.Token);
        var authorization = remainsAuthorized is null
            ? null
            : BrowserProxy.MonitorAuthorizationAsync(remainsAuthorized, cts.Token, AuthorizationInterval);

        var running = authorization is null
            ? new[] { pushing, draining }
            : new[] { pushing, draining, authorization };
        var completed = await Task.WhenAny(running);
        var revoked = authorization is not null &&
            ReferenceEquals(completed, authorization) && !await authorization;
        await cts.CancelAsync();
        try { await Task.WhenAll(running); } catch (OperationCanceledException) { } catch (WebSocketException) { }

        if (revoked && client.State == WebSocketState.Open)
        {
            log.LogInformation("Session event socket closed for {SessionId}: access revoked", sessionId);
            await client.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                "session access changed", CancellationToken.None);
        }
    }

    private static async Task PushAsync(
        WebSocket client, ISessionEventSubscription subscription, CancellationToken ct)
    {
        // An immediate first event closes the window between the client's initial REST read and
        // the subscription: anything that changed in between is picked up by the resulting
        // refetch, so the client never needs a poll to recover from a race at connect time.
        await SendAsync(client, SessionEventKinds.Files, ct);

        var events = subscription.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        // Carried across iterations deliberately. A keep-alive tick must not start a second
        // MoveNextAsync while the first is still outstanding — an async enumerator permits only
        // one in flight, and the extra call would either throw or silently drop the event the
        // first one was about to yield.
        Task<bool>? pending = null;
        try
        {
            while (true)
            {
                pending ??= events.MoveNextAsync().AsTask();
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                // The keep-alive is what detects a connection a proxy dropped without telling
                // either end; an idle session can otherwise go minutes without traffic, and the
                // client would believe it is live while receiving nothing.
                if (await Task.WhenAny(pending, Task.Delay(KeepAlive, idle.Token)) != pending)
                {
                    await SendAsync(client, "ping", ct);
                    continue;
                }

                await idle.CancelAsync();
                var advanced = await pending;
                pending = null;
                if (!advanced) return;
                await SendAsync(client, events.Current, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { await events.DisposeAsync(); }
    }

    /// <summary>
    /// The client has nothing to say, but the frames still have to be read: without a pending
    /// receive a close from the browser is never observed and the socket lingers until the
    /// request is aborted.
    /// </summary>
    private static async Task DrainAsync(WebSocket client, CancellationToken ct)
    {
        var buffer = new byte[256];
        try
        {
            while (client.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await client.ReceiveAsync(buffer, ct);
                if (message.MessageType == WebSocketMessageType.Close) return;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    // Explicit: SerializeToUtf8Bytes does not pick up the MVC pipeline's camelCase policy, and
    // the default would put "Type" on the wire while the client reads "type".
    private static readonly JsonSerializerOptions Wire =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static Task SendAsync(WebSocket client, string kind, CancellationToken ct)
    {
        if (client.State != WebSocketState.Open) return Task.CompletedTask;
        var frame = JsonSerializer.SerializeToUtf8Bytes(new SessionEventMessage(kind), Wire);
        return client.SendAsync(frame, WebSocketMessageType.Text, true, ct);
    }

    private sealed record SessionEventMessage(string Type);
}
