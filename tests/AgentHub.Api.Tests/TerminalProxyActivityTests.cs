using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AgentHub.Api.Models;
using AgentHub.Api.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentHub.Api.Tests;

public class TerminalProxyActivityTests
{
    [Theory]
    [InlineData("?activity=1", "/", true)]
    [InlineData("", "/", false)]
    [InlineData("?activity=1", "/shell", false)]
    public async Task ViewerReceivesOptedInActivityButCannotSendInput(string query, string path, bool subscribed)
    {
        var received = Channel.CreateUnbounded<string>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        async Task Agent(Microsoft.AspNetCore.Http.HttpContext ctx)
        {
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync("tty");
            await socket.SendAsync(Encoding.UTF8.GetBytes("terminal ready"), WebSocketMessageType.Text, true, ctx.RequestAborted);
            using var agentTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var buffer = new byte[4096];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    var message = await socket.ReceiveAsync(buffer, agentTimeout.Token);
                    if (message.MessageType == WebSocketMessageType.Close) break;
                    var text = Encoding.UTF8.GetString(buffer, 0, message.Count);
                    received.Writer.TryWrite(text);
                    await socket.SendAsync(Encoding.UTF8.GetBytes("activity snapshot"), WebSocketMessageType.Text, true, ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException ex) when (agentTimeout.IsCancellationRequested)
            {
                app.Logger.LogDebug(ex, "Recording agent reached its test connection timeout.");
            }
            catch (WebSocketException ex) when (socket.State == WebSocketState.Aborted)
            {
                app.Logger.LogDebug(ex, "Recording agent connection was aborted during test cleanup.");
            }
        }
        app.Map("/", Agent);
        app.Map("/shell", Agent);
        // A real network connection exercises both forwarding directions and the query opt-in.
        app.Map("/viewer", ctx => TerminalProxy.HandleAsync(ctx, new SessionInfo
        {
            Id = "s1", Title = "Session", Owner = "owner", Mode = SessionMode.Interactive,
            Phase = "Running", PodIp = "127.0.0.1"
        }, false, app.Services.GetRequiredService<ILoggerFactory>(), new Uri(app.Urls.Single()).Port, path == "/" ? "/" : path));
        await app.StartAsync();
        using var browser = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await browser.ConnectAsync(new Uri(app.Urls.Single().Replace("http:", "ws:") + "/viewer" + query), timeout.Token);
        var buffer = new byte[4096];
        var ready = await browser.ReceiveAsync(buffer, timeout.Token);
        Assert.Equal("terminal ready", Encoding.UTF8.GetString(buffer, 0, ready.Count));
        if (subscribed)
        {
            Assert.Equal("{\"type\":\"subscribe\",\"channel\":\"activity\"}", await received.Reader.ReadAsync(timeout.Token));
            var snapshot = await browser.ReceiveAsync(buffer, timeout.Token);
            Assert.Equal("activity snapshot", Encoding.UTF8.GetString(buffer, 0, snapshot.Count));
        }
        foreach (var input in new[] { "{\"type\":\"input\",\"data\":\"danger\"}", "{\"type\":\"resize\",\"cols\":1,\"rows\":1}", "{\"type\":\"subscribe\",\"channel\":\"activity\"}" })
            await browser.SendAsync(Encoding.UTF8.GetBytes(input), WebSocketMessageType.Text, true, timeout.Token);
        using var noInput = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await received.Reader.ReadAsync(noInput.Token));
        browser.Abort();
        await app.StopAsync(timeout.Token);
    }
}
