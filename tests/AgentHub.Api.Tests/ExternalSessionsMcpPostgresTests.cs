using System.Reflection;
using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Mcp;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;

namespace AgentHub.Api.Tests;

public class ExternalSessionsMcpPostgresTests
{
    [PostgreSqlTheory]
    [InlineData(ShareRole.Viewer)]
    [InlineData(ShareRole.Collaborator)]
    public async Task BothSurfacesResolveDirectInheritedAndProjectGrantsAndAuditExternalSends(ShareRole role)
    {
        await using var db = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await db.AddUserAsync("alice");
        await db.AddUserAsync("bob");
        await db.AddSessionAsync("bob", "b");
        await db.UpsertSessionAsync(new SessionRecord { CallbackToken = "proof-callback", Id = "child", Owner = "bob", Title = "child", ParentSessionId = "b" });
        var project = (await db.Projects.CreateAsync("bob", new CreateProjectRequest("Shared", null))).Id;
        await db.UpsertSessionAsync(new SessionRecord { CallbackToken = "proof-callback", Id = "project", Owner = "bob", Title = "project", ProjectId = project });
        await db.Shares.UpsertDirectAsync("bob", "b", "alice", role);
        await new ProjectSharingService(db.Shares, new FakeEnterpriseLicense(true))
            .ShareAsync("bob", project, ProjectShareSubjectType.User, "alice", role);
        var (remote, tools, messages) = await Clients(db);
        var listed = Assert.IsAssignableFrom<IReadOnlyList<SessionInfo>>(
            Assert.IsType<OkObjectResult>((await remote.List(default)).Result).Value);
        Assert.Equal(new[] { "b", "child", "project" }, listed.Select(s => s.Id).Order().ToArray());
        Assert.Equal(3, (await tools.ListAgents()).Count);
        foreach (var session in listed)
        {
            Assert.Equal("bob", session.Owner);
            Assert.Equal(role.ToString(), session.AccessRole);
            Assert.Null(session.Prompt);
            Assert.Null(session.McpConfigJson);
            Assert.Equal(role.ToString(), (await tools.GetSession(session.Id)).AccessRole);
            Assert.IsType<OkObjectResult>((await remote.Get(session.Id, default)).Result);
            if (role == ShareRole.Viewer)
            {
                Assert.IsType<NotFoundResult>(await remote.SendMessage(session.Id, new("denied"), default));
                await Assert.ThrowsAsync<McpException>(() => tools.SendToAgent(session.Id, "denied"));
            }
            else
            {
                Assert.IsType<OkObjectResult>(await remote.SendMessage(session.Id, new("remote"), default));
                Assert.Equal("inbox", (await tools.SendToAgent(session.Id, "tool")).DeliveredVia);
                Assert.IsType<BadRequestObjectResult>(await remote.SendMessage(session.Id, new("denied", Priority: true), default));
                await Assert.ThrowsAsync<McpException>(() => tools.SendToAgent(session.Id, "denied", interrupt: "true"));
                var rows = await messages.ListPageAsync("bob", session.Id, null, 10);
                Assert.Equal(2, rows.Count);
                Assert.All(rows, m => { Assert.Equal("alice", m.FromOwner); Assert.Null(m.FromSessionId); Assert.False(m.Priority); });
            }
        }
        Assert.Equal(role == ShareRole.Collaborator ? 6L : 0L,
            await db.ScalarAsync<long>("SELECT count(*) FROM cross_owner_message_audit"));
        await db.Shares.DeleteDirectAsync("bob", "b", "alice");
        Assert.IsType<NotFoundResult>((await remote.Get("child", default)).Result);
        await Assert.ThrowsAsync<McpException>(() => tools.SendToAgent("child", "revoked"));
    }

    [PostgreSqlFact]
    public async Task EveryListedOwnedPeerParentChildAndSiblingUsesOwnedRecordsAndCanReceive()
    {
        await using var db = await PostgresSharingDatabase.CreateAsync(pooling: false);
        foreach (var (id, parent) in new (string, string?)[] { ("peer", null), ("parent", null), ("a", "parent"), ("child", "a"), ("sibling", "parent") })
            await db.UpsertSessionAsync(new SessionRecord { CallbackToken = "proof-callback", Id = id, Owner = "alice", Title = id, ParentSessionId = parent, ProjectId = "fleet" });
        var (remote, tools, messages) = await Clients(db);
        var listed = await tools.ListSessions();
        Assert.Equal(5, listed.Count);
        foreach (var session in listed)
        {
            Assert.Equal("fleet", session.ProjectId);
            Assert.Equal("alice", session.Owner);
            Assert.Equal("fleet", (await tools.GetSession(session.Id)).ProjectId);
            Assert.IsType<OkObjectResult>(await remote.SendMessage(session.Id, new("remote"), default));
            await tools.SendToAgent(session.Id, "tool");
            Assert.Equal(2, (await messages.ListPageAsync("alice", session.Id, null, 10)).Count);
        }
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM cross_owner_message_audit"));
    }

    [PostgreSqlFact]
    public async Task LinksUseBearerRolesAndHistoryAndExternalAuditWithoutNamedAccess()
    {
        await using var db = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await db.AddSessionAsync("bob", "b");
        var (remote, _, messages) = await Clients(db);
        var service = DispatchProxy.Create<ISessionService, OwnedSessions>();
        ((OwnedSessions)service).Store = db.Sessions;
        var access = new SessionAccessService(db.Shares, new FakeEnterpriseLicense(true));
        var links = new ExternalShareLinkService(access, new SharedSessionHistory(service), messages);
        foreach (var role in new[] { ShareRole.Viewer, ShareRole.Collaborator })
        {
            var issued = await db.Shares.CreateLinkAsync("bob", "b", role, null);
            foreach (var value in new[] { issued.Token, "https://example.com/shared/" + issued.Token })
            {
                var read = await links.ReadAsync(value, null, null, default);
                Assert.NotNull(read);
                Assert.Equal(role.ToString(), read.Session.AccessRole);
                Assert.Equal("history", read.History.Text);
            }
            var sent = await links.SendAsync(issued.Token, "link message", default);
            if (role == ShareRole.Viewer) Assert.Null(sent);
            else
            {
                Assert.NotNull(sent);
                var row = Assert.Single(await messages.ListPageAsync("bob", "b", null, 10));
                Assert.Equal("via share link", row.FromTitle);
                Assert.Null(row.FromSessionId);
                Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM cross_owner_message_audit WHERE sender_owner='via share link'"));
            }
            await db.Shares.DeleteLinkAsync("bob", "b", issued.Link.Id);
            Assert.Null(await links.ReadAsync(issued.Token, null, null, default));
            Assert.Null(await links.SendAsync(issued.Token, "revoked", default));
        }
        var expired = await db.Shares.CreateLinkAsync("bob", "b", ShareRole.Collaborator, null);
        await db.ScalarAsync<long>("WITH expired AS (UPDATE session_share_links SET expires_at=now()-interval '1 minute' RETURNING id) SELECT count(*) FROM expired");
        Assert.Null(await links.ReadAsync(expired.Token, null, null, default));
        Assert.Null(await links.SendAsync(expired.Token, "expired", default));
        Assert.IsType<NotFoundResult>((await remote.Get("b", default)).Result);
        Assert.Null(await links.ReadAsync("invalid", null, null, default));
    }

    private static async Task<(RemoteController, AgentHubMcpTools, PostgresSessionMessageStore)> Clients(PostgresSharingDatabase db)
    {
        var service = DispatchProxy.Create<ISessionService, OwnedSessions>();
        ((OwnedSessions)service).Store = db.Sessions;
        var messages = new PostgresSessionMessageStore(db.Configuration);
        await messages.InitializeAsync();
        var access = new SessionAccessService(db.Shares, new FakeEnterpriseLicense(true));
        var sharing = new SessionSharingService(db.Shares, new FakeEnterpriseLicense(true), null);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "proof"))
        };
        context.Request.Headers.Authorization = "Bearer oah_proof";
        var remote = new RemoteController((_, _) => Task.FromResult<RemoteCaller?>(new("alice", null)), service,
            messages, access: access, sharing: sharing) { ControllerContext = new() { HttpContext = context } };
        var tools = new AgentHubMcpTools(new FixedContext { HttpContext = context }, service,
            NullLogger<AgentHubMcpTools>.Instance, messages, access: access, sharing: sharing);
        return (remote, tools, messages);
    }

    private sealed class FixedContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    public class OwnedSessions : DispatchProxy
    {
        public PostgresSessionStore Store { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(ISessionService.ListSessionsAsync) => List((string)args![0]!),
            nameof(ISessionService.GetSessionAsync) => Get((string)args![0]!, (string)args[1]!),
            nameof(ISessionService.TouchActivityAsync) => Task.CompletedTask,
            nameof(ISessionService.GetConversationAsync) => Task.FromResult<IReadOnlyList<TranscriptEntry>?>(null),
            nameof(ISessionService.GetTranscriptAsync) => Task.FromResult<string?>("history"),
            _ => throw new InvalidOperationException($"Unexpected service operation: {method?.Name}")
        };
        private async Task<IReadOnlyList<SessionInfo>> List(string owner)
            => (await Store.ListAsync(owner)).Select(Info).ToList();
        private async Task<SessionInfo?> Get(string owner, string id)
            => await Store.GetAsync(owner, id) is { } record ? Info(record) : null;
        private static SessionInfo Info(SessionRecord r) => new()
        {
            Id = r.Id, Owner = r.Owner, Title = r.Title, Mode = r.Mode,
            Phase = r.Status, ProjectId = r.ProjectId, ParentSessionId = r.ParentSessionId
        };
    }
}
