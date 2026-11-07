using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using AgentHub.Api.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// #520: a terminal upgrade waited on the session read for as long as the Kubernetes client
/// allowed (100 s against a stalled API server). The browser held that handshake, and Chrome
/// queued every other WebSocket of the page behind it, so the Workspace could not reconnect.
/// </summary>
public class TerminalUpgradeDeadlineTests
{
    public class Sessions : DispatchProxy
    {
        public Func<CancellationToken, Task<SessionInfo?>> Read = _ => Task.FromResult<SessionInfo?>(null);
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(ISessionService.GetSessionAsync) => Read((CancellationToken)args![2]!),
            nameof(ISessionService.ClearQuestionAsync) => Task.CompletedTask,
            nameof(ISessionService.TouchActivityAsync) => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        };
    }

    private static async Task<(HttpStatusCode? Status, string? RetryAfter, TimeSpan Took, bool Opened)> Upgrade(
        Func<CancellationToken, Task<SessionInfo?>> read)
    {
        var sessions = DispatchProxy.Create<ISessionService, Sessions>();
        ((Sessions)(object)sessions).Read = read;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/", async ctx =>
        {
            using var agent = await ctx.WebSockets.AcceptWebSocketAsync("tty");
            await agent.SendAsync(Encoding.UTF8.GetBytes("ready"), WebSocketMessageType.Text, true, ctx.RequestAborted);
            await agent.ReceiveAsync(new byte[16], ctx.RequestAborted);
        });
        app.Map("/terminal", ctx => TerminalProxy.HandleAsync(ctx, "owner", "s1", sessions, TestPods.Live(new HttpClient()),
            app.Services.GetRequiredService<ILoggerFactory>(), new Uri(app.Urls.Single()).Port));
        await app.StartAsync();

        using var browser = new ClientWebSocket();
        browser.Options.CollectHttpResponseDetails = true;
        browser.Options.AddSubProtocol("tty");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clock = Stopwatch.StartNew();
        var opened = false;
        try
        {
            await browser.ConnectAsync(new Uri(app.Urls.Single().Replace("http:", "ws:") + "/terminal"), timeout.Token);
            opened = true;
        }
        catch (WebSocketException) { }
        var took = clock.Elapsed;
        var retryAfter = browser.HttpResponseHeaders?.TryGetValue("Retry-After", out var values) == true ? string.Join(",", values) : null;
        var status = opened ? (HttpStatusCode?)null : browser.HttpStatusCode;
        browser.Abort();
        await app.StopAsync(CancellationToken.None);
        return (status, retryAfter, took, opened);
    }

    [Fact]
    public async Task ASessionReadThatDoesNotFinishIsAnswered503WithinTheDeadline()
    {
        var (status, retryAfter, took, opened) = await Upgrade(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });

        Assert.False(opened);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("2", retryAfter);
        Assert.InRange(took, TerminalProxy.SessionReadTimeout - TimeSpan.FromMilliseconds(200), TerminalProxy.SessionReadTimeout + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task AnUnknownSessionIsStill404()
    {
        var (status, _, _, opened) = await Upgrade(_ => Task.FromResult<SessionInfo?>(null));
        Assert.False(opened);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task AReadThatFinishesInTimeStillUpgrades()
    {
        var (_, _, _, opened) = await Upgrade(async ct =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            return new SessionInfo { Id = "s1", Title = "S", Owner = "owner", Mode = SessionMode.Interactive, Phase = "Running", PodIp = "127.0.0.1" };
        });
        Assert.True(opened);
    }
}
