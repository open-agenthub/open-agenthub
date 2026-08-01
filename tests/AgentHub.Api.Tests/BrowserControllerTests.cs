using System.Net;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Persistence;
using AgentHub.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class BrowserControllerTests
{
    [Fact]
    public async Task Start_ReturnsUnauthorized_WhenOnlySessionIdIsKnown()
    {
        var controller = Controller(null, new RecordingBrowserService(Connection()));

        var result = await controller.Start("session-1", CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task Start_UsesAuthorizedSessionAndReturnsOneConnection()
    {
        var browser = new RecordingBrowserService(Connection());
        var controller = Controller(Session(), browser);

        var result = Assert.IsType<OkObjectResult>(
            await controller.Start("session-1", CancellationToken.None));

        Assert.Same(browser.Connection, result.Value);
        Assert.Equal(1, browser.EnsureCalls);
        Assert.Equal(IPAddress.Parse("10.0.0.8"), browser.AgentPodIp);
    }

    [Fact]
    public async Task Stop_DoesNotTouchBrowser_WhenRequestIsUnauthorized()
    {
        var browser = new RecordingBrowserService(Connection());
        var controller = Controller(null, browser);

        Assert.IsType<UnauthorizedResult>(
            await controller.Stop("session-1", CancellationToken.None));
        Assert.Equal(0, browser.StopCalls);
    }

    [Fact]
    public async Task LeaseState_RejectsOversizedTokenBeforeLookup()
    {
        var browser = new RecordingBrowserService(Connection());
        var controller = new BrowserLeaseController(browser)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Browser-Token"] = new string('x', 513);

        Assert.IsType<UnauthorizedResult>(
            await controller.State("lease-1", CancellationToken.None));
        Assert.Equal(0, browser.MintCalls);
    }

    [Fact]
    public async Task LeaseState_ReturnsOnlyUrlsForMatchingToken()
    {
        var browser = new RecordingBrowserService(Connection())
        {
            StateUrls = new BrowserStateUrls("get", "put")
        };
        var controller = new BrowserLeaseController(browser)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Browser-Token"] = "browser-token";

        var result = Assert.IsType<OkObjectResult>(
            await controller.State("lease-1", CancellationToken.None));
        Assert.Same(browser.StateUrls, result.Value);
        Assert.Equal(1, browser.MintCalls);
    }

    [Fact]
    public async Task TerminalAgentStatus_StopsBrowser()
    {
        var session = Session();
        var browser = new RecordingBrowserService(Connection());
        var controller = new InternalController(
            new CallbackStore(session), [], null!, null!, [], [], null!, null!, browser)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Agent-Token"] = session.CallbackToken;

        Assert.IsType<NoContentResult>(await controller.Status(
            session.Id, new InternalController.StatusBody("Succeeded"), CancellationToken.None));
        Assert.Equal(1, browser.StopCalls);
    }

    private static BrowserController Controller(SessionRecord? authorized, RecordingBrowserService browser)
    {
        var controller = new BrowserController(new FixedAuthorizer(authorized), browser)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.8");
        controller.Request.Headers["X-Agent-Token"] = "agent-token";
        return controller;
    }

    private static SessionRecord Session() => new()
    {
        Id = "session-1", Owner = "owner", CallbackToken = "agent-token",
        AgentSessionId = "thread", Mode = SessionMode.Interactive
    };

    private static BrowserConnection Connection() => new(
        new BrowserSummary(BrowserPhase.Running), "http://10.0.0.9:9222", "10.0.0.9");

    private sealed class CallbackStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(session.Owner == owner && session.Id == id ? session : null);
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(session.Id == id ? session : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(session.CallbackToken == token ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([session]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FixedAuthorizer(SessionRecord? session) : IBrowserRequestAuthorizer
    {
        public Task<SessionRecord?> AuthorizeAsync(string sessionId, string? token,
            IPAddress sourceIp, CancellationToken ct = default) =>
            Task.FromResult(session?.Id == sessionId ? session : null);
    }

    private sealed class RecordingBrowserService(BrowserConnection connection) : IBrowserService
    {
        public BrowserConnection Connection { get; } = connection;
        public int EnsureCalls { get; private set; }
        public int StopCalls { get; private set; }
        public IPAddress? AgentPodIp { get; private set; }
        public int MintCalls { get; private set; }
        public BrowserStateUrls? StateUrls { get; init; }
        public Task<BrowserConnection> EnsureAsync(SessionRecord session, IPAddress agentPodIp, CancellationToken ct = default)
        { EnsureCalls++; AgentPodIp = agentPodIp; return Task.FromResult(Connection); }
        public Task<BrowserSummary> GetSummaryAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Connection.Browser);
        public Task<IReadOnlyDictionary<string, BrowserSummary>> GetSummariesAsync(
            IReadOnlyCollection<string> sessionIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, BrowserSummary>>(new Dictionary<string, BrowserSummary>());
        public Task<BrowserConnection?> GetConnectionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<BrowserConnection?>(Connection);
        public Task ResizeAsync(string sessionId, BrowserViewport viewport, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(string sessionId, CancellationToken ct = default)
        { StopCalls++; return Task.CompletedTask; }
        public Task<BrowserStateUrls?> MintStateUrlsAsync(string leaseId, string token,
            CancellationToken ct = default)
        { MintCalls++; return Task.FromResult(StateUrls); }
        public Task DeleteStateAsync(SessionRecord session, CancellationToken ct = default) => Task.CompletedTask;
    }
}