using System.Net.WebSockets;
using AgentHub.Api.Browser;

namespace AgentHub.Api.WebSockets;

public static class BrowserProxy
{
    public static async Task HandleAsync(HttpContext context, string sessionId, bool canWrite,
        IBrowserService browsers, ILoggerFactory loggerFactory,
        Func<CancellationToken, Task<bool>>? remainsAuthorized = null)
    {
        var log = loggerFactory.CreateLogger("BrowserProxy");
        var connection = await browsers.GetConnectionAsync(sessionId, context.RequestAborted);
        var requestedBinary = context.WebSockets.WebSocketRequestedProtocols
            .Contains("binary", StringComparer.OrdinalIgnoreCase);
        using var client = await context.WebSockets.AcceptWebSocketAsync(
            requestedBinary ? "binary" : null);

        if (connection is null)
        {
            var summary = await browsers.GetSummaryAsync(sessionId, context.RequestAborted);
            log.LogWarning("Browser unavailable for session {SessionId} ({FailureCode})",
                sessionId, summary.FailureCode ?? summary.Phase.ToString());
            await client.CloseAsync(WebSocketCloseStatus.EndpointUnavailable,
                "browser unavailable", CancellationToken.None);
            return;
        }

        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol("binary");
        try
        {
            await upstream.ConnectAsync(
                new Uri($"ws://{connection.PodIp}:{UpstreamPort(canWrite)}/"), context.RequestAborted);
        }
        catch (Exception error)
        {
            log.LogWarning("Browser upstream failed for session {SessionId} ({FailureCode})",
                sessionId, error.GetType().Name);
            await client.CloseAsync(WebSocketCloseStatus.EndpointUnavailable,
                "browser unreachable", CancellationToken.None);
            return;
        }

        await RelayAsync(client, upstream, canWrite, context.RequestAborted, remainsAuthorized);
    }

    public static int UpstreamPort(bool canWrite) => canWrite ? 6080 : 6082;

    public static async Task RelayAsync(WebSocket client, WebSocket upstream, bool canWrite,
        CancellationToken ct, Func<CancellationToken, Task<bool>>? remainsAuthorized = null,
        TimeSpan? authorizationInterval = null)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var toClient = PumpAsync(upstream, client, relayCts.Token);
        var toUpstream = PumpAsync(client, upstream, relayCts.Token);
        var authorization = remainsAuthorized is null ? null :
            MonitorAuthorizationAsync(remainsAuthorized, relayCts.Token, authorizationInterval);
        var completed = await Task.WhenAny(
            authorization is null ? [toClient, toUpstream] : [toClient, toUpstream, authorization]);
        var revoked = authorization is not null && ReferenceEquals(completed, authorization) &&
            !await authorization;
        relayCts.Cancel();
        try
        {
            await Task.WhenAll(authorization is null
                ? [toClient, toUpstream]
                : [toClient, toUpstream, authorization]);
        }
        catch (OperationCanceledException) { }
        if (revoked && client.State == WebSocketState.Open)
            await client.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                "browser access changed", CancellationToken.None);
    }

    public static async Task<bool> MonitorAuthorizationAsync(
        Func<CancellationToken, Task<bool>> remainsAuthorized, CancellationToken ct,
        TimeSpan? interval = null)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval ?? TimeSpan.FromSeconds(2), ct);
                if (!await remainsAuthorized(ct)) return false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch { return false; }
        return true;
    }

    private static async Task PumpAsync(WebSocket source, WebSocket destination, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        await Task.Yield();
        try
        {
            while (source.State == WebSocketState.Open &&
                   destination.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await source.ReceiveAsync(buffer, ct);
                if (message.MessageType == WebSocketMessageType.Close) return;
                await destination.SendAsync(
                    new ArraySegment<byte>(buffer, 0, message.Count),
                    message.MessageType, message.EndOfMessage, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }
}