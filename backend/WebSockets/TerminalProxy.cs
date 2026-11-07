using System.Net.WebSockets;
using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Services;

namespace AgentHub.Api.WebSockets;

/// <summary>
/// Bidirectional proxy: the browser only connects to the backend (authenticated),
/// and the backend opens the actual connection to the agent pod in the cluster.
/// The pod is not reachable from outside thanks to a NetworkPolicy.
/// </summary>
public static class TerminalProxy
{
    /// <summary>
    /// How often a held socket renews the session's last-activity timestamp. One write a minute
    /// per socket is nothing; one per keystroke would be thousands for a value the sweep reads
    /// once a minute (docs/session-expiry.md).
    /// </summary>
    public static readonly TimeSpan ActivityInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long an upgrade may wait for the session read (pod phase and IP from the Kubernetes
    /// API) before it is answered. The client's library would otherwise wait its full 100 s on a
    /// slow API server, and the browser holds the handshake for as long: Chrome queues every
    /// other WebSocket of the page to this host behind it, so the Workspace could not reconnect
    /// at all (#520, docs/websocket-handshake-timeout.md). With the 5 s pod confirmation this
    /// stays under the browser's 10 s handshake limit.
    /// </summary>
    public static readonly TimeSpan SessionReadTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The session for a terminal upgrade, or null once the request has been answered: 404 for an
    /// unknown session, 503 with Retry-After when the read did not finish in
    /// <see cref="SessionReadTimeout"/>. A 503 fails the handshake like a starting pod's 409, so
    /// the browser's ordinary retry takes it from there.
    /// </summary>
    public static async Task<SessionInfo?> ReadForUpgradeAsync(HttpContext ctx,
        Func<CancellationToken, Task<SessionInfo?>> read, ILogger log, TimeSpan? timeout = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        deadline.CancelAfter(timeout ?? SessionReadTimeout);
        SessionInfo? session;
        try
        {
            session = await read(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ctx.RequestAborted.IsCancellationRequested)
        {
            log.LogWarning("Terminal upgrade answered 503: the session read took longer than {Timeout}", timeout ?? SessionReadTimeout);
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers.RetryAfter = "2";
            await ctx.Response.WriteAsync("Session status not available yet; retry.");
            return null;
        }
        if (session is null) ctx.Response.StatusCode = 404;
        return session;
    }

    public static async Task HandleAsync(HttpContext ctx, string owner, string sessionId,
        ISessionService sessions, IAgentPodRequests pods, ILoggerFactory lf, int agentPort, string upstreamPath = "/")
    {
        var log = lf.CreateLogger("TerminalProxy");

        var session = await ReadForUpgradeAsync(ctx, ct => sessions.GetSessionAsync(owner, sessionId, ct), log);
        if (session is null) return;
        if (string.IsNullOrEmpty(session.PodIp) || session.Phase != "Running")
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsync($"Session not ready (phase={session.Phase}).");
            return;
        }
        if (!await PodConfirmedAsync(ctx, pods, session, log)) return;

        // Opening the terminal counts as "seen" — clear the waiting-for-reply flag so
        // the badge does not stick around after the user engages with the session.
        try { await sessions.ClearQuestionAsync(owner, sessionId, ctx.RequestAborted); } catch { }

        using var client = await ctx.WebSockets.AcceptWebSocketAsync();
        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol("tty");

        try
        {
            await upstream.ConnectAsync(new Uri($"ws://{session.PodIp}:{agentPort}{upstreamPath}"), ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Upstream connection to {Pod} failed", session.PodIp);
            await client.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "agent unreachable", CancellationToken.None);
            return;
        }

        // The attach itself and anything typed afterwards is the owner using the session.
        var activity = new ActivityThrottle(ct => sessions.TouchActivityAsync(owner, sessionId, ct), ActivityInterval);
        await activity.SignalAsync(ctx.RequestAborted);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var pumpToAgent = Pump(client, upstream, cts.Token, activity);
        var pumpToBrowser = Pump(upstream, client, cts.Token);

        await Task.WhenAny(pumpToAgent, pumpToBrowser);
        cts.Cancel();
    }

    /// <summary>
    /// Proxy for an already access-checked session (sharing): read-only unless canWrite.
    /// <paramref name="onActivity"/> is called on attach and, throttled, for every frame the
    /// browser sends into the session; null when nothing should be recorded (a read-only viewer
    /// is watching, not using).
    /// </summary>
    public static async Task HandleAsync(HttpContext ctx, SessionInfo session, bool canWrite,
        IAgentPodRequests pods, ILoggerFactory lf, int agentPort, string upstreamPath = "/",
        Func<CancellationToken, Task>? onActivity = null)
    {
        var log = lf.CreateLogger("TerminalProxy");
        if (string.IsNullOrEmpty(session.PodIp) || session.Phase != "Running")
        {
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsync($"Session not ready (phase={session.Phase}).");
            return;
        }
        if (!await PodConfirmedAsync(ctx, pods, session, log)) return;
        using var client = await ctx.WebSockets.AcceptWebSocketAsync();
        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol("tty");
        try
        {
            await upstream.ConnectAsync(new Uri($"ws://{session.PodIp}:{agentPort}{upstreamPath}"), ctx.RequestAborted);
            // Subscribe on behalf of viewers without opening a browser-to-agent input channel.
            // The query opt-in keeps older terminals from rendering metadata as terminal output.
            if (!canWrite && upstreamPath == "/" && ctx.Request.Query["activity"] == "1")
                await upstream.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"subscribe\",\"channel\":\"activity\"}"),
                    WebSocketMessageType.Text, true, ctx.RequestAborted);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Upstream connection to {Pod} failed", session.PodIp);
            await client.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "agent unreachable", CancellationToken.None);
            return;
        }
        var activity = onActivity is null || !canWrite ? null : new ActivityThrottle(onActivity, ActivityInterval);
        if (activity is not null) await activity.SignalAsync(ctx.RequestAborted);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        var toBrowser = Pump(upstream, client, cts.Token);
        if (canWrite)
        {
            var toAgent = Pump(client, upstream, cts.Token, activity);
            await Task.WhenAny(toAgent, toBrowser);
        }
        else await toBrowser;
        cts.Cancel();
    }
    /// <summary>
    /// The stored IP can already belong to another session's pod after a replacement, and what
    /// the person types (secrets included) would go there (#229). Checked once per connection:
    /// a replaced pod drops this TCP connection, and the browser's reconnect is checked again.
    /// Refused before the upgrade, so the browser sees the same 409 as a session that is not ready.
    /// </summary>
    private static async Task<bool> PodConfirmedAsync(HttpContext ctx, IAgentPodRequests pods, SessionInfo session, ILogger log)
    {
        try
        {
            await pods.ConfirmAsync(session.Id, session.PodIp, ctx.RequestAborted);
            return true;
        }
        catch (AgentPodNotConfirmedException error)
        {
            log.LogWarning("Terminal for session {Id} not opened: {Reason}", session.Id, error.Message);
            ctx.Response.StatusCode = 409;
            await ctx.Response.WriteAsync("Session pod not confirmed; it may be restarting.");
            return false;
        }
    }

    private static async Task Pump(WebSocket from, WebSocket to, CancellationToken ct, ActivityThrottle? activity = null)
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
                // After the forward, so a slow touch never delays what was typed. Resize and
                // keepalive frames count too; distinguishing them would mean parsing every frame
                // for a timestamp that is renewed once a minute anyway.
                if (activity is not null) await activity.SignalAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }
}
