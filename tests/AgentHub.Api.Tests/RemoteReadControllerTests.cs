using System.Reflection;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Library;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class RemoteReadControllerTests
{
    public class Calls : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handle = (_, _) => throw new InvalidOperationException();
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handle(method!, args);
    }

    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> handle) where T : class
    {
        var proxy = DispatchProxy.Create<T, Calls>();
        ((Calls)(object)proxy).Handle = handle;
        return proxy;
    }

    private static RemoteReadController Controller(string? header = "Bearer oah_valid", bool restricted = false,
        Func<MethodInfo, object?[]?, object?>? sessions = null,
        Func<MethodInfo, object?[]?, object?>? projects = null,
        Func<MethodInfo, object?[]?, object?>? library = null,
        Func<MethodInfo, object?[]?, object?>? messages = null,
        Func<MethodInfo, object?[]?, object?>? access = null,
        Func<MethodInfo, object?[]?, object?>? live = null)
    {
        object? Unexpected(MethodInfo method, object?[]? args) => throw new InvalidOperationException(method.Name);
        var controller = new RemoteReadController((token, _) => Task.FromResult(token == "oah_valid"
                ? new RemoteCaller("alice", restricted ? new ApiTokenScope() : null) : null),
            Proxy<ISessionService>(sessions ?? Unexpected), Proxy<IProjectStore>(projects ?? Unexpected),
            Proxy<ILibraryAccess>(library ?? Unexpected), Proxy<ISessionMessageStore>(messages ?? Unexpected),
            Proxy<ISessionAccessService>(access ?? Unexpected), Proxy<ILiveTranscriptReader>(live ?? Unexpected))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        if (header is not null) controller.Request.Headers.Authorization = header;
        return controller;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic oah_valid")]
    [InlineData("Bearer jwt")]
    [InlineData("Bearer oah_invalid")]
    public async Task InvalidAuthNeverTouchesDependencies(string? header)
    {
        var c = Controller(header);
        Assert.IsType<UnauthorizedResult>((await c.Projects(default)).Result);
        Assert.IsType<UnauthorizedResult>((await c.Project("p", default)).Result);
        Assert.IsType<UnauthorizedResult>((await c.McpServers(default)).Result);
        Assert.IsType<UnauthorizedResult>((await c.Messages("s", null, null, default)).Result);
        Assert.IsType<UnauthorizedResult>((await c.Conversation("s", null, null, default)).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectReadsAlwaysUseTokenOwner(bool restricted)
    {
        var c = Controller(restricted: restricted, projects: (method, args) =>
        {
            Assert.Equal("alice", args![0]);
            return method.Name == "ListAsync" ? Task.FromResult<IReadOnlyList<ProjectInfo>>([])
                : Task.FromResult<ProjectInfo?>(null);
        });
        Assert.IsType<OkObjectResult>((await c.Projects(default)).Result);
        Assert.IsType<NotFoundResult>((await c.Project("foreign", default)).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogProjectsOnlyIdsAndNamesFromAccessService(bool restricted)
    {
        var c = Controller(restricted: restricted, library: (method, args) =>
        {
            Assert.Equal("ListMcpServersAsync", method.Name);
            Assert.Equal("alice", args![0]);
            return Task.FromResult<IReadOnlyList<McpServerRecord>>([new()
            {
                Id = "shared", Name = "Shared", Owner = "bob", Kind = "http",
                ConfigJson = "private config", SecretJson = "private headers"
            }]);
        });
        var result = Assert.IsType<OkObjectResult>((await c.McpServers(default)).Result);
        Assert.Equal(new RemoteMcpServerInfo("shared", "Shared"),
            Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<RemoteMcpServerInfo>>(result.Value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedConversationUsesLiveReaderAndWorkspaceCursor(bool restricted)
    {
        var c = Controller(restricted: restricted, sessions: (method, args) =>
        {
            Assert.Equal("alice", args![0]);
            Assert.Equal("GetSessionAsync", method.Name);
            return Task.FromResult<SessionInfo?>(new() { Id = "s", Title = "Session", Owner = "alice", Mode = SessionMode.Interactive, Phase = "Running" });
        }, live: (_, _) => Task.FromResult<IReadOnlyList<TranscriptEntry>?>([
            new("user", "one", null), new("assistant", "two", null), new("assistant", "three", null)]));
        var result = Assert.IsType<OkObjectResult>((await c.Conversation("s", 1, 1, default)).Result);
        var page = Assert.IsType<ConversationPage>(result.Value);
        Assert.Equal("native", page.Source);
        Assert.Equal("two", Assert.Single(page.Entries).Text);
        Assert.Equal(2, page.NextOffset);
        Assert.True(page.Truncated);
    }

    [Fact]
    public async Task UnownedConversationRequiresExistingShareResolver()
    {
        var c = Controller(sessions: (_, _) => Task.FromResult<SessionInfo?>(null), access: (method, args) =>
        {
            Assert.Equal("ResolveUserAsync", method.Name);
            Assert.Equal("alice", args![0]);
            return Task.FromResult<SessionAccessResult?>(null);
        });
        Assert.IsType<NotFoundResult>((await c.Conversation("foreign", null, null, default)).Result);
        Assert.IsType<NotFoundResult>((await c.Messages("foreign", null, null, default)).Result);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SharedConversationUsesExistingLicenceAndGrantRules(bool licensed, bool granted)
    {
        var store = Proxy<ISessionAccessStore>((_, args) =>
        {
            Assert.Equal("alice", args![0]);
            Assert.Equal("shared", args[1]);
            return Task.FromResult(granted ? new StoredSessionAccess(new SessionRecord
            {
                Id = "shared", Owner = "bob", Title = "Shared", Mode = SessionMode.Interactive,
                AgentSessionId = "native", CallbackToken = "callback", Status = "Succeeded"
            }, ShareRole.Viewer) : null);
        });
        var resolver = new SessionAccessService(store, new FakeEnterpriseLicense(licensed));
        var c = Controller(restricted: true, sessions: (method, args) =>
        {
            if (method.Name == "GetSessionAsync") return Task.FromResult<SessionInfo?>(null);
            Assert.Equal("bob", args![0]);
            return Task.FromResult<IReadOnlyList<TranscriptEntry>?>([new("assistant", "shared turn", null)]);
        }, access: (_, args) => resolver.ResolveUserAsync((string)args![0]!, (string)args[1]!, default));
        var result = (await c.Conversation("shared", null, null, default)).Result;
        if (licensed && granted)
        {
            var page = Assert.IsType<ConversationPage>(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal("shared turn", Assert.Single(page.Entries).Text);
        }
        else Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ConversationFallsBackToWorkspaceScrollbackPaging()
    {
        var c = Controller(sessions: (method, _) => method.Name switch
        {
            "GetSessionAsync" => Task.FromResult<SessionInfo?>(new()
            {
                Id = "s", Title = "Session", Owner = "alice", Mode = SessionMode.Interactive, Phase = "Succeeded"
            }),
            "GetConversationAsync" => Task.FromResult<IReadOnlyList<TranscriptEntry>?>(null),
            "GetTranscriptAsync" => Task.FromResult<string?>("abcdef"),
            _ => throw new InvalidOperationException(method.Name)
        }, live: (_, _) => Task.FromResult<IReadOnlyList<TranscriptEntry>?>(null));
        var page = Assert.IsType<ConversationPage>(
            Assert.IsType<OkObjectResult>((await c.Conversation("s", 2, 2, default)).Result).Value);
        Assert.Equal("scrollback", page.Source);
        Assert.Equal("cd", page.Text);
        Assert.Equal(4, page.NextOffset);
    }

    [Theory]
    [InlineData("bad", 20)]
    [InlineData(null, 0)]
    [InlineData(null, 101)]
    public async Task BadPagingNeverReachesMessages(string? cursor, int limit)
    {
        var c = Controller(sessions: (_, _) => Task.FromResult<SessionInfo?>(new() { Id = "s", Title = "Session", Owner = "alice", Mode = SessionMode.Interactive, Phase = "Succeeded" }));
        Assert.IsType<BadRequestObjectResult>((await c.Messages("s", cursor, limit, default)).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InboxUsesOwnerBoundReadWithoutAcknowledging(bool restricted)
    {
        var c = Controller(restricted: restricted,
            sessions: (_, _) => Task.FromResult<SessionInfo?>(new() { Id = "s", Title = "Session", Owner = "alice", Mode = SessionMode.Interactive, Phase = "Succeeded" }),
            messages: (method, args) =>
            {
                Assert.Equal("ListPageAsync", method.Name);
                Assert.Equal("alice", args![0]);
                Assert.Equal("s", args[1]);
                Assert.Equal(2, args[3]);
                return Task.FromResult<IReadOnlyList<SessionMessageRecord>>([
                    new() { Id = "2", Owner = "alice", ToSessionId = "s", Body = "two" },
                    new() { Id = "1", Owner = "alice", ToSessionId = "s", Body = "one" }]);
            });
        var result = Assert.IsType<OkObjectResult>((await c.Messages("s", null, 1, default)).Result);
        var page = Assert.IsType<RemoteMessagePage>(result.Value);
        Assert.Equal("2", Assert.Single(page.Messages).Id);
        Assert.NotNull(page.NextCursor);
        Assert.Null(page.Messages[0].DeliveredAt);
    }
}
