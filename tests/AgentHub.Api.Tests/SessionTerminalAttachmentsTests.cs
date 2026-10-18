using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
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

public class SessionTerminalAttachmentsTests
{
    private const string FileId = "0123456789abcdef0123456789abcdef";

    public class SessionProxy : DispatchProxy
    {
        public SessionInfo? Live;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(ISessionService.GetSessionAsync) ? Task.FromResult(Live) : throw new NotSupportedException();
    }
    private sealed class Access : ISessionAccessService
    {
        public SessionAccessResult? Resolved;
        public Task<SessionAccessResult?> ResolveUserAsync(string principal, string id, CancellationToken ct = default)
            => Task.FromResult(Resolved);
        public Task<SessionAccessResult?> ResolveTokenAsync(string token, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SessionAccessResult?> ResolveTokenReadOnlyAsync(string token, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
    private sealed class Relay : ISessionTerminalAttachments
    {
        public readonly List<IReadOnlyList<string>> Calls = [];
        public (int Status, string Body)? Answer = (200, "{\"attachments\":[]}");
        public Task<(int Status, string Body)?> MaterializeAsync(SessionRecord session, IReadOnlyList<string> fileIds, CancellationToken ct)
        { Calls.Add(fileIds); return Task.FromResult(Answer); }
    }
    private sealed class Pod(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Sent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("secret", request.Headers.GetValues("X-Agent-Token").Single());
            Assert.Equal("http://127.0.0.1:7681/agenthub/attachments", request.RequestUri!.ToString());
            Sent = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
    private static readonly SessionRecord Record = new() { Id = "s1", Owner = "owner", CallbackToken = "secret" };
    private static ControllerContext Context() => new() { HttpContext = new DefaultHttpContext
    { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "owner")], "test")) } };
    private static SessionTerminalAttachmentsController Controller(SessionAccessLevel level, Relay relay) =>
        new(new Access { Resolved = new(Record, level, null) }, relay) { ControllerContext = Context() };

    [Theory]
    [InlineData(SessionAccessLevel.Owner, "content")]
    [InlineData(SessionAccessLevel.Collaborator, "content")]
    [InlineData(SessionAccessLevel.Viewer, "forbid")]
    [InlineData(SessionAccessLevel.None, "notfound")]
    public async Task OnlyThoseWhoMayTypeIntoTheTerminalAndUploadReachThePod(SessionAccessLevel level, string expected)
    {
        var relay = new Relay();
        var result = await Controller(level, relay).Materialize("s1", new([FileId]), default);
        switch (expected)
        {
            case "content": Assert.IsType<ContentResult>(result); break;
            case "forbid": Assert.IsType<ForbidResult>(result); break;
            default: Assert.IsType<NotFoundResult>(result); break;
        }
        Assert.Equal(expected == "content" ? 1 : 0, relay.Calls.Count);
    }

    public static TheoryData<List<string>?> InvalidIds => new()
    {
        null, new List<string>(), new List<string> { "nope" }, new List<string> { FileId, FileId },
        Enumerable.Range(0, 6).Select(i => i.ToString("x32")).ToList()
    };

    [Theory]
    [MemberData(nameof(InvalidIds))]
    public async Task MalformedIdsNeverReachThePod(List<string>? ids)
    {
        var relay = new Relay();
        Assert.IsType<BadRequestObjectResult>(await Controller(SessionAccessLevel.Owner, relay).Materialize("s1", new(ids), default));
        Assert.Empty(relay.Calls);
    }

    [Theory]
    [InlineData(404, 501)]
    [InlineData(409, 409)]
    [InlineData(422, 422)]
    [InlineData(400, 400)]
    [InlineData(502, 502)]
    [InlineData(500, 502)]
    public async Task PodRefusalsKeepTheirMeaningAndAnOlderRuntimeIsUnsupported(int pod, int expected)
    {
        var relay = new Relay { Answer = (pod, "{\"error\":\"x\"}") };
        var result = await Controller(SessionAccessLevel.Owner, relay).Materialize("s1", new([FileId]), default);
        var status = result switch { ContentResult c => c.StatusCode, ObjectResult o => o.StatusCode, _ => null };
        Assert.Equal(expected, status);
    }

    [Fact]
    public async Task AStoppedPodIs503()
    {
        var relay = new Relay { Answer = null };
        Assert.Equal(503, Assert.IsType<ObjectResult>(await Controller(SessionAccessLevel.Owner, relay).Materialize("s1", new([FileId]), default)).StatusCode);
    }

    [Fact]
    public async Task ProxyPostsTheIdsWithTheCallbackTokenToTheStoredPod()
    {
        var sessions = DispatchProxy.Create<ISessionService, SessionProxy>();
        ((SessionProxy)sessions).Live = new SessionInfo
        { Id = "s1", Owner = "owner", Title = "Session", Mode = SessionMode.Interactive, Phase = "Running", PodIp = "127.0.0.1" };
        var pod = new Pod("{\"attachments\":[{\"id\":\"" + FileId + "\",\"path\":\"/workspace/x.png\"}]}");
        using var http = new HttpClient(pod);
        var proxy = new SessionTerminalAttachments(http, sessions, new ConfigurationBuilder().Build());
        var answer = await proxy.MaterializeAsync(Record, [FileId], default);
        Assert.NotNull(answer);
        var (status, body) = answer.Value;
        Assert.Equal(200, status);
        Assert.Contains("/workspace/x.png", body);
        Assert.Equal(FileId, JsonDocument.Parse(pod.Sent!).RootElement.GetProperty("fileIds")[0].GetString());
        ((SessionProxy)sessions).Live = null;
        Assert.Null(await proxy.MaterializeAsync(Record, [FileId], default));
    }

    [Fact]
    public async Task AnOversizedPodAnswerIsDropped()
    {
        var sessions = DispatchProxy.Create<ISessionService, SessionProxy>();
        ((SessionProxy)sessions).Live = new SessionInfo
        { Id = "s1", Owner = "owner", Title = "Session", Mode = SessionMode.Interactive, Phase = "Running", PodIp = "127.0.0.1" };
        using var http = new HttpClient(new Pod(new string(' ', 70 * 1024)));
        var proxy = new SessionTerminalAttachments(http, sessions, new ConfigurationBuilder().Build());
        Assert.Null(await proxy.MaterializeAsync(Record, [FileId], default));
    }
}
