using System.Net;
using System.Security.Claims;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class BrowserClipboardControllerTests
{
    [Theory]
    [InlineData(SessionAccessLevel.Owner)]
    [InlineData(SessionAccessLevel.Collaborator)]
    public async Task Paste_SendsTextToTheRunningBrowserPod(SessionAccessLevel level)
    {
        var runtime = new RecordingRuntime();
        var controller = UserController(new FixedAccess(level), runtime);

        var result = await controller.Paste(
            "session-1", new BrowserClipboardPasteRequest("hello\nworld"), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(("10.0.0.7", "hello\nworld"), runtime.Pasted);
    }

    [Fact]
    public async Task Copy_ReturnsTheSelectionWithoutLettingAnyCacheKeepIt()
    {
        var runtime = new RecordingRuntime { Selection = "selected" };
        var controller = UserController(new FixedAccess(SessionAccessLevel.Collaborator), runtime);

        var result = await controller.Copy(
            "session-1", new BrowserClipboardCopyRequest(true), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(new BrowserClipboardText("selected"), ok.Value);
        Assert.Equal(("10.0.0.7", true), runtime.Copied);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task Viewer_CannotPushOrPullTheClipboard()
    {
        var runtime = new RecordingRuntime { Selection = "secret" };
        var controller = UserController(new FixedAccess(SessionAccessLevel.Viewer), runtime);

        var paste = await controller.Paste(
            "session-1", new BrowserClipboardPasteRequest("x"), CancellationToken.None);
        var copy = await controller.Copy(
            "session-1", new BrowserClipboardCopyRequest(false), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(paste).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(copy).StatusCode);
        Assert.Null(runtime.Pasted);
        Assert.Null(runtime.Copied);
    }

    [Fact]
    public async Task UnknownSession_IsHidden()
    {
        var runtime = new RecordingRuntime();
        var controller = UserController(new FixedAccess(null), runtime);

        var result = await controller.Paste(
            "session-1", new BrowserClipboardPasteRequest("x"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Null(runtime.Pasted);
    }

    [Fact]
    public async Task StoppedBrowser_IsAConflict()
    {
        var controller = UserController(
            new FixedAccess(SessionAccessLevel.Owner), new RecordingRuntime(), connection: null);

        var result = await controller.Copy(
            "session-1", new BrowserClipboardCopyRequest(false), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Paste_RejectsMissingOrOversizedTextBeforeReachingThePod()
    {
        var runtime = new RecordingRuntime();
        var controller = UserController(new FixedAccess(SessionAccessLevel.Owner), runtime);

        var missing = await controller.Paste(
            "session-1", new BrowserClipboardPasteRequest(null), CancellationToken.None);
        var oversized = await controller.Paste("session-1",
            new BrowserClipboardPasteRequest(new string('x', BrowserClipboardApi.MaxTextLength + 1)),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(missing);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge,
            Assert.IsType<StatusCodeResult>(oversized).StatusCode);
        Assert.Null(runtime.Pasted);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, StatusCodes.Status413PayloadTooLarge)]
    [InlineData(HttpStatusCode.BadRequest, StatusCodes.Status400BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError, StatusCodes.Status502BadGateway)]
    public async Task RuntimeFailures_MapToClientOrGatewayErrors(HttpStatusCode runtimeStatus, int expected)
    {
        var runtime = new RecordingRuntime
        {
            Error = new HttpRequestException("runtime", null, runtimeStatus)
        };
        var controller = UserController(new FixedAccess(SessionAccessLevel.Owner), runtime);

        var result = await controller.Copy(
            "session-1", new BrowserClipboardCopyRequest(false), CancellationToken.None);

        Assert.Equal(expected, Assert.IsType<StatusCodeResult>(result).StatusCode);
    }

    [Theory]
    [InlineData(SessionAccessLevel.Collaborator, true)]
    [InlineData(SessionAccessLevel.Viewer, false)]
    public async Task ShareLink_ReachesTheClipboardOnlyWhenItGrantsControl(
        SessionAccessLevel level, bool allowed)
    {
        var runtime = new RecordingRuntime { Selection = "shared" };
        var controller = new SharedBrowserClipboardController(
            new FixedAccess(level), new ConnectedBrowser(Connection()), runtime)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Copy(
            "link-token", new BrowserClipboardCopyRequest(false), CancellationToken.None);

        if (allowed)
            Assert.Equal(new BrowserClipboardText("shared"), Assert.IsType<OkObjectResult>(result).Value);
        else
            Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal(allowed, runtime.Copied is not null);
    }

    private static BrowserConnection Connection() =>
        new(new BrowserSummary(BrowserPhase.Running), "http://10.0.0.7:9222", "10.0.0.7");

    private static BrowserClipboardController UserController(
        ISessionAccessService access, IBrowserRuntimeClient runtime, bool connected = true) =>
        UserController(access, runtime, connected ? Connection() : null);

    private static BrowserClipboardController UserController(
        ISessionAccessService access, IBrowserRuntimeClient runtime, BrowserConnection? connection)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("preferred_username", "someone")], "test"))
        };
        return new BrowserClipboardController(access, new ConnectedBrowser(connection), runtime)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    private sealed class FixedAccess(SessionAccessLevel? level) : ISessionAccessService
    {
        private SessionAccessResult? Result => level is null ? null : new(new SessionRecord
        {
            Id = "session-1", Owner = "owner", CallbackToken = "token",
            AgentSessionId = "agent-session", Mode = SessionMode.Interactive
        }, level.Value, null);

        public Task<SessionAccessResult?> ResolveUserAsync(
            string principal, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(sessionId == "session-1" ? Result : null);
        public Task<SessionAccessResult?> ResolveTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(Result);
    }

    private sealed class RecordingRuntime : IBrowserRuntimeClient
    {
        public (string PodIp, string Text)? Pasted { get; private set; }
        public (string PodIp, bool Cut)? Copied { get; private set; }
        public string Selection { get; init; } = "";
        public Exception? Error { get; init; }

        public Task ResizeAsync(string podIp, BrowserViewport viewport, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task PasteAsync(string podIp, string text, CancellationToken ct = default)
        {
            if (Error is not null) return Task.FromException(Error);
            Pasted = (podIp, text);
            return Task.CompletedTask;
        }
        public Task<string> CopyAsync(string podIp, bool cut, CancellationToken ct = default)
        {
            if (Error is not null) return Task.FromException<string>(Error);
            Copied = (podIp, cut);
            return Task.FromResult(Selection);
        }
    }

    private sealed class ConnectedBrowser(BrowserConnection? connection) : IBrowserService
    {
        public Task<BrowserConnection?> GetConnectionAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(connection);
        public Task<BrowserConnection> EnsureAsync(
            SessionRecord session, IPAddress agentPodIp, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BrowserSummary> GetSummaryAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, BrowserSummary>> GetSummariesAsync(
            IReadOnlyCollection<string> sessionIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ResizeAsync(string sessionId, BrowserViewport viewport, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task StopAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BrowserStateUrls?> MintStateUrlsAsync(
            string leaseId, string token, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteStateAsync(SessionRecord session, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
