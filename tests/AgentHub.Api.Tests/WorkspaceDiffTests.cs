using System.Net;
using System.Reflection;
using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHub.Api.Tests;

public class WorkspaceDiffTests
{
    public class SessionProxy : DispatchProxy
    {
        public SessionInfo? Live;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(ISessionService.GetSessionAsync) ? Task.FromResult(Live) : throw new NotSupportedException();
    }
    private sealed class Access : ISessionAccessService
    {
        public SessionAccessResult? Resolved;
        public int Reads;
        public Task<SessionAccessResult?> ResolveUserAsync(string principal, string id, CancellationToken ct = default)
            => Task.FromResult(Resolved);
        public Task<SessionAccessResult?> ResolveTokenAsync(string token, CancellationToken ct = default)
            => throw new InvalidOperationException("Shared reads must not record a visit");
        public Task<SessionAccessResult?> ResolveTokenReadOnlyAsync(string token, CancellationToken ct = default)
        { Reads++; return Task.FromResult(Resolved); }
    }
    private sealed class Reader : IWorkspaceDiffReader
    {
        public int Calls;
        public string? Body = "{\"repositories\":[]}";
        public Task<string?> ReadAsync(SessionRecord session, string comparison, CancellationToken ct)
        { Calls++; return Task.FromResult(Body); }
    }
    private sealed class Pod(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Yield();
            Calls++;
            Assert.Equal("secret", request.Headers.GetValues("X-Agent-Token").Single());
            Assert.Equal("http://127.0.0.1:7681/agenthub/diff?comparison=head", request.RequestUri!.ToString());
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
    private static readonly SessionRecord Record = new() { Id = "s1", Owner = "owner", CallbackToken = "secret" };
    private static ControllerContext Context() => new() { HttpContext = new DefaultHttpContext
    { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "owner")], "test")) } };

    [Theory]
    [InlineData(SessionAccessLevel.Owner)]
    [InlineData(SessionAccessLevel.Collaborator)]
    [InlineData(SessionAccessLevel.Viewer)]
    public async Task FileReadersCanReadDiffsAndRevocationIsCheckedEveryTime(SessionAccessLevel level)
    {
        var access = new Access { Resolved = new(Record, level, null) };
        var reader = new Reader();
        var controller = new WorkspaceDiffController(access, reader) { ControllerContext = Context() };
        Assert.IsType<ContentResult>(await controller.Read("s1", default));
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        var shared = new SharedWorkspaceDiffController(access, reader) { ControllerContext = Context() };
        Assert.IsType<ContentResult>(await shared.Read("link", default));
        access.Resolved = null;
        Assert.IsType<NotFoundResult>(await shared.Read("link", default));
        Assert.IsType<NotFoundResult>(await controller.Read("s1", default));
        Assert.Equal(2, reader.Calls); Assert.Equal(2, access.Reads);
    }

    [Fact]
    public async Task InvalidComparisonsAreRejectedAndUnavailablePodsReturn503()
    {
        var access = new Access { Resolved = new(Record, SessionAccessLevel.Owner, null) };
        var reader = new Reader { Body = null };
        var controller = new WorkspaceDiffController(access, reader) { ControllerContext = Context() };
        Assert.IsType<BadRequestResult>(await controller.Read("s1", default, "../secret"));
        Assert.Equal(0, reader.Calls);
        Assert.Equal(503, Assert.IsType<ObjectResult>(await controller.Read("s1", default)).StatusCode);
    }

    [Theory]
    [InlineData(200, 20, true)]
    [InlineData(404, 20, false)]
    [InlineData(200, 2100000, false)]
    public async Task ProxyUsesStoredPodAndTokenAndBoundsResponses(int status, int length, bool available)
    {
        var sessions = DispatchProxy.Create<ISessionService, SessionProxy>();
        ((SessionProxy)sessions).Live = new SessionInfo
        { Id = "s1", Owner = "owner", Title = "Session", Mode = SessionMode.Interactive, Phase = "Running", PodIp = "127.0.0.1" };
        var pod = new Pod(new string(' ', length), (HttpStatusCode)status);
        using var http = new HttpClient(pod);
        var reader = new WorkspaceDiffReader(TestPods.Live(http), sessions, new ConfigurationBuilder().Build());
        Assert.Equal(available, await reader.ReadAsync(Record, "head", default) is not null);
        ((SessionProxy)sessions).Live = null;
        Assert.Null(await reader.ReadAsync(Record, "head", default));
        Assert.Equal(1, pod.Calls);
    }
}
