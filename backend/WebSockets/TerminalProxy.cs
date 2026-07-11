using System.Net.WebSockets;
using AgentHub.Api.Services;

namespace AgentHub.Api.WebSockets;

/// <summary>
/// Bidirectional proxy: the browser only connects to the backend (authenticated),
/// and the backend opens the actual connection to the agent pod in the cluster.
/// The pod is not reachable from outside thanks to a NetworkPolicy.
/// </summary>
public static class TerminalProxy
{
    public static async Task HandleAsync(HttpContext ctx, string owner, string sessionId,
        ISessionService sessions, ILoggerFactory lf, int agentPort)
    {
        var log = lf.CreateLogger("TerminalProxy");

        var session = await sessions.GetSessionAsync(owner, sessionId, ctx.RequestAborted);
        if (session is null) { ctx.Response.StatusCode = 404; return; }
        if (string.IsNullOrEmpty(session.PodIp) || session.Phase != "Running")
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsync($"Session not ready (phase={session.Phase}).");
            return;
        }

        using var client = await ctx.WebSockets.AcceptWebSocketAsync();
        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol("tty");

        try
        {
            await upstream.ConnectAsync(new Uri($"ws://{session.PodIp}:{agentPort}/"), ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Upstream connection to {Pod} failed", session.PodIp);
            await client.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "agent unreachable", CancellationToken.None);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var pumpToAgent = Pump(client, upstream, cts.Token);
        var pumpToBrowser = Pump(upstream, client, cts.Token);

        await Task.WhenAny(pumpToAgent, pumpToBrowser);
        cts.Cancel();
    }

    private static async Task Pump(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (from.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = await from.ReceiveAsync(buf, ct);
                if (msg.MessageType == WebSocketMessageType.Close)
                {
                    if (to.State == WebSocketState.Open)
                        await to.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
                    return;
                }
                if (to.State == WebSocketState.Open)
                    await to.SendAsync(new ArraySegment<byte>(buf, 0, msg.Count), msg.MessageType, msg.EndOfMessage, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }
}
