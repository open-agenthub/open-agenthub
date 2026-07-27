using System.Security.Claims;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class BrowserViewportControllerTests
{
    [Fact]
    public async Task Resize_AcceptsOwnedSessionAndValidViewport()
    {
        var browser = new RecordingBrowserService();
        var controller = Controller("owner", Session(), browser);

        var result = await controller.Resize(
            "session-1", new BrowserViewportRequest(800, 600), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("session-1", browser.SessionId);
        Assert.Equal(new BrowserViewport(800, 600), browser.Viewport);
    }

    [Fact]
    public async Task Resize_HidesSessionOwnedByAnotherUser()
    {
        var browser = new RecordingBrowserService();
        var controller = Controller("intruder", Session(), browser);

        var result = await controller.Resize(
            "session-1", new BrowserViewportRequest(800, 600), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Null(browser.SessionId);
    }

    [Theory]
    [InlineData(479, 600)]
    [InlineData(800, 319)]
    [InlineData(2561, 600)]
    [InlineData(800, 1601)]
    public async Task Resize_RejectsViewportOutsideRuntimeBounds(int width, int height)
    {
        var browser = new RecordingBrowserService();
        var controller = Controller("owner", Session(), browser);

        var result = await controller.Resize(
            "session-1", new BrowserViewportRequest(width, height), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(browser.SessionId);
    }

    [Fact]
    public async Task Resize_ReturnsConflictWhenBrowserIsNotRunning()
    {
        var browser = new RecordingBrowserService
        {
            Error = new InvalidOperationException("Browser is not running.")
        };
        var controller = Controller("owner", Session(), browser);

        var result = await controller.Resize(
            "session-1", new BrowserViewportRequest(800, 600), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Resize_ReturnsBadGatewayWhenRuntimeFails()
    {
        var browser = new RecordingBrowserService
        {
            Error = new HttpRequestException("runtime unavailable")
        };
        var controller = Controller("owner", Session(), browser);

        var result = await controller.Resize(
            "session-1", new BrowserViewportRequest(800, 600), CancellationToken.None);

        Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, ((ObjectResult)result).StatusCode);
    }

    private static BrowserViewportController Controller(
        string owner, SessionRecord session, IBrowserService browser)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("preferred_username", owner)], "test"))
        };
        return new BrowserViewportController(new SessionStore(session), browser)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private static SessionRecord Session() => new()
    {
        Id = "session-1",
        Owner = "owner",
        CallbackToken = "token",
        AgentSessionId = "agent-session",
        Mode = SessionMode.Interactive
    };

    private sealed class SessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(
                session.Owner == owner && session.Id == id ? session : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingBrowserService : IBrowserService
    {
        public string? SessionId { get; private set; }
        public BrowserViewport? Viewport { get; private set; }
        public Exception? Error { get; init; }

        public Task ResizeAsync(
            string sessionId, BrowserViewport viewport, CancellationToken ct = default)
        {
            SessionId = sessionId;
            Viewport = viewport;
            return Error is null ? Task.CompletedTask : Task.FromException(Error);
        }

        public Task<BrowserConnection> EnsureAsync(
            SessionRecord session, System.Net.IPAddress agentPodIp, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BrowserSummary> GetSummaryAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, BrowserSummary>> GetSummariesAsync(
            IReadOnlyCollection<string> sessionIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BrowserConnection?> GetConnectionAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task StopAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BrowserStateUrls?> MintStateUrlsAsync(
            string leaseId, string token, CancellationToken ct = default) =>
            throw new NotSupportedException();        public Task DeleteStateAsync(SessionRecord session, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
