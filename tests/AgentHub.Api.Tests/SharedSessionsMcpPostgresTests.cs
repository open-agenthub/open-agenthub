using AgentHub.Api.Files;
using System.Reflection;
using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public class SharedSessionsMcpPostgresTests
{
    [PostgreSqlFact]
    public async Task EveryListedOwnedFleetTargetIsReadableAndSendable()
    {
        await using var db = await Setup();
        foreach (var (id, project, parent) in new (string, string?, string?)[]
        {
            ("a", "fleet", "parent"), ("peer", "fleet", null), ("parent", null, null),
            ("child", null, "a"), ("sibling", "fleet", "parent"), ("outside", "other", null)
        })
            await db.UpsertSessionAsync(new SessionRecord
            {
                Id = id, Owner = "alice", Title = id, Mode = SessionMode.Interactive,
                CallbackToken = $"callback-{id}", ProjectId = project, ParentSessionId = parent
            });
        var caller = Controller(db, "a", ownedFleet: true);
        var listed = Assert.IsAssignableFrom<IEnumerable<ProjectAgentInfo>>(
            Assert.IsType<OkObjectResult>(await caller.ProjectAgents("a", default)).Value);
        Assert.All(listed, s => Assert.Equal("alice", s.Owner));
        var targets = listed.Where(s => !s.Self).Select(s => s.Id).Order().ToArray();
        Assert.Equal(new[] { "child", "parent", "peer", "sibling" }, targets);
        foreach (var target in targets)
        {
            Assert.IsType<OkObjectResult>(await caller.SendMessage("a", new(target, "fleet"), default));
            Assert.IsType<OkObjectResult>(await caller.GetPeer("a", target, default));
            Assert.Single(await new PostgresSessionMessageStore(db.Configuration)
                .ListPageAsync("alice", target, null, 10));
        }
        foreach (var target in new[] { "outside", "missing", "b" })
        {
            Assert.IsType<NotFoundResult>(await caller.SendMessage("a", new(target, "denied"), default));
            Assert.IsType<NotFoundResult>(await caller.GetPeer("a", target, default));
        }
    }

    [PostgreSqlFact]
    public async Task ViewerCanReadButCannotSendOrManageAndRevocationIsImmediate()
    {
        await using var db = await Setup();
        var controller = Controller(db, "a");
        await db.Shares.UpsertDirectAsync("bob", "b", "alice", ShareRole.Viewer);
        var directory = Assert.IsType<OkObjectResult>(await controller.ProjectAgents("a", default));
        Assert.Contains(Assert.IsAssignableFrom<IEnumerable<ProjectAgentInfo>>(directory.Value), s => s.Id == "b" && s.AccessRole == "Viewer");
        var read = Assert.IsType<OkObjectResult>(await controller.GetPeer("a", "b", default));
        var safe = Assert.IsType<SharedSessionInfo>(read.Value);
        Assert.Equal("Viewer", safe.AccessRole);
        Assert.Null(safe.McpConfigJson);
        Assert.IsType<NotFoundResult>(await controller.SendMessage("a", new("b", "hello"), default));
        await AssertManagementDenied(controller);
        await db.Shares.DeleteDirectAsync("bob", "b", "alice");
        Assert.IsType<NotFoundResult>(await controller.GetPeer("a", "b", default));
        Assert.IsType<NotFoundResult>(await controller.SendMessage("a", new("b", "hello"), default));
    }

    [PostgreSqlFact]
    public async Task CollaboratorQueuesAttributedMessageAndAuditAtomicallyButCannotInterruptOrManage()
    {
        await using var db = await Setup();
        var controller = Controller(db, "a");
        await db.Shares.UpsertDirectAsync("bob", "b", "alice", ShareRole.Collaborator);
        Assert.IsType<OkObjectResult>(await controller.GetPeer("a", "b", default));
        Assert.IsType<BadRequestObjectResult>(await controller.SendMessage("a", new("b", "hello", Priority: true), default));
        Assert.IsType<BadRequestObjectResult>(await controller.SendMessage("a", new("b", "hello", Interrupt: true), default));
        await AssertManagementDenied(controller);
        Assert.IsType<OkObjectResult>(await controller.SendMessage("a", new("b", "hello"), default));
        var messages = new PostgresSessionMessageStore(db.Configuration);
        var message = Assert.Single(await messages.ListPageAsync("bob", "b", null, 10));
        Assert.Equal("alice", message.FromOwner);
        Assert.Equal("a (owner: alice)", message.FromTitle);
        Assert.False(message.Priority);
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM cross_owner_message_audit WHERE sender_owner='alice' AND recipient_owner='bob'"));
        var inbox = Assert.IsType<OkObjectResult>(await Controller(db, "b").InboxMessages("b", default));
        var json = JsonSerializer.Serialize(inbox.Value);
        Assert.Contains("alice", json);
        Assert.Contains("a (owner: alice)", json);
        await db.Shares.DeleteDirectAsync("bob", "b", "alice");
        Assert.IsType<NotFoundResult>(await controller.SendMessage("a", new("b", "after revocation"), default));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM cross_owner_message_audit"));
    }

    [PostgreSqlFact]
    public async Task LinkAndUnlicensedNamedShareGrantNoMcpAccess()
    {
        await using var db = await Setup();
        await db.Shares.CreateLinkAsync("bob", "b", ShareRole.Collaborator, null);
        var controller = Controller(db, "a");
        Assert.IsType<NotFoundResult>(await controller.GetPeer("a", "b", default));
        Assert.IsType<NotFoundResult>(await controller.SendMessage("a", new("b", "link"), default));
        await db.Shares.UpsertDirectAsync("bob", "b", "alice", ShareRole.Collaborator);
        controller = Controller(db, "a", licensed: false);
        Assert.IsType<NotFoundResult>(await controller.GetPeer("a", "b", default));
        Assert.IsType<NotFoundResult>(await controller.SendMessage("a", new("b", "unlicensed"), default));
    }

    [PostgreSqlFact]
    public async Task NamedRolesNeverAuthorizeForeignCallbackTools()
    {
        await using var db = await Setup();
        foreach (var role in new[] { ShareRole.Viewer, ShareRole.Collaborator })
        {
            await db.Shares.UpsertDirectAsync("bob", "b", "alice", role);
            var caller = Controller(db, "a");
            Assert.IsType<UnauthorizedResult>(await caller.Spawn("b", new(), default));
            Assert.IsType<UnauthorizedResult>((await caller.ClearContext("b", new("continue"), default)).Result);
            Assert.IsType<UnauthorizedResult>(await caller.Secrets("b", default));
            Assert.IsType<UnauthorizedResult>(await caller.CreateSecretRequest("b", new("key", "file", "proof"), default));
            Assert.IsType<UnauthorizedResult>(await caller.GetSecretRequest("b", "request", default));
            Assert.IsType<UnauthorizedResult>(await caller.ExpireSecretRequest("b", "request", default));
            Assert.IsType<UnauthorizedResult>(await caller.InboxMessages("b", default));
            var containers = new SessionContainersController(new AgentCallbackAuthorizer(db.Sessions), null!)
            { ControllerContext = caller.ControllerContext };
            Assert.IsType<UnauthorizedResult>(await containers.List("b", default));
            Assert.IsType<UnauthorizedResult>(await containers.Run("b", new("db", "example.com/image", null, null,
                null, null, null, null, null, null), default));
            Assert.IsType<UnauthorizedResult>(await containers.Logs("b", "db", null, null, default));
            Assert.IsType<UnauthorizedResult>(await containers.Stop("b", "db", default));
        }
    }

    [PostgreSqlFact]
    public async Task AuditFailureRollsBackTheMessage()
    {
        await using var db = await Setup();
        await db.Shares.UpsertDirectAsync("bob", "b", "alice", ShareRole.Collaborator);
        await db.ExecuteAsync("ALTER TABLE cross_owner_message_audit ADD CONSTRAINT proof_reject CHECK (sender_owner <> 'alice')");
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            Controller(db, "a").SendMessage("a", new("b", "must roll back"), default));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM session_messages"));
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM cross_owner_message_audit"));
    }

    [PostgreSqlFact]
    public async Task AccountPurgeRemovesCrossOwnerMetadataForEitherPartyEvenAfterSenderSessionDeletion()
    {
        foreach (var principal in new[] { "alice", "bob" })
        {
            await using var db = await Setup();
            await db.Shares.UpsertDirectAsync("bob", "b", "alice", ShareRole.Collaborator);
            Assert.IsType<OkObjectResult>(await Controller(db, "a").SendMessage("a", new("b", "purge proof"), default));
            await new PostgresSessionMessageStore(db.Configuration).AddAsync(new SessionMessageRecord
            {
                Id = "unrelated", FromSessionId = "c", ToSessionId = "d", Owner = "dave",
                FromOwner = "carol", FromTitle = "control", Body = "keep"
            });
            await db.Sessions.DeleteAsync("a");
            var purge = new AccountPurgeStore(db.Configuration,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AccountPurgeStore>.Instance);
            await purge.PurgeAsync(principal, await purge.ListSessionIdsAsync(principal));
            Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM session_messages"));
            Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM cross_owner_message_audit"));
            Assert.Equal("unrelated", await db.ScalarAsync<string>("SELECT message_id FROM cross_owner_message_audit"));
        }
    }

    [PostgreSqlFact]
    public async Task InheritedChildRolesUseTheSameDecisionForDirectoryReadSendAndLiveRevocation()
    {
        await using var db = await Setup();
        await db.AddSessionAsync("bob", "c");
        var child = (await db.GetSessionAsync("bob", "c"))!;
        child.ParentSessionId = "b";
        await db.UpsertSessionAsync(child);
        var caller = Controller(db, "a");
        await db.Shares.UpsertDirectAsync("bob", "b", "alice", ShareRole.Viewer);
        var directory = Assert.IsType<OkObjectResult>(await caller.ProjectAgents("a", default));
        Assert.Contains(Assert.IsAssignableFrom<IEnumerable<ProjectAgentInfo>>(directory.Value),
            s => s.Id == "c" && s.AccessRole == "Viewer");
        var status = Assert.IsType<SharedSessionInfo>(Assert.IsType<OkObjectResult>(
            await caller.GetPeer("a", "c", default)).Value);
        Assert.Equal("Viewer", status.AccessRole);
        Assert.IsType<NotFoundResult>(await caller.SendMessage("a", new("c", "viewer"), default));
        Assert.IsType<NotFoundResult>(await caller.DeletePeer("a", "c", default));
        Assert.IsType<NotFoundResult>(await caller.ConvertPeer("a", "c", new(), default));

        // A direct viewer grant must not mask the stronger collaborator grant inherited live.
        await db.Shares.UpsertDirectAsync("bob", "c", "alice", ShareRole.Viewer);
        await db.Shares.UpsertDirectAsync("bob", "b", "alice", ShareRole.Collaborator);
        Assert.IsType<OkObjectResult>(await caller.SendMessage("a", new("c", "collaborator"), default));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM cross_owner_message_audit WHERE recipient_session_id='c'"));
        await db.Shares.DeleteDirectAsync("bob", "b", "alice");
        status = Assert.IsType<SharedSessionInfo>(Assert.IsType<OkObjectResult>(
            await caller.GetPeer("a", "c", default)).Value);
        Assert.Equal("Viewer", status.AccessRole);
        Assert.IsType<NotFoundResult>(await caller.SendMessage("a", new("c", "downgraded"), default));
        await db.Shares.DeleteDirectAsync("bob", "c", "alice");
        Assert.IsType<NotFoundResult>(await caller.GetPeer("a", "c", default));
        directory = Assert.IsType<OkObjectResult>(await caller.ProjectAgents("a", default));
        Assert.DoesNotContain(Assert.IsAssignableFrom<IEnumerable<ProjectAgentInfo>>(directory.Value), s => s.Id == "c");
        await db.Shares.CreateLinkAsync("bob", "b", ShareRole.Collaborator, null);
        Assert.IsType<NotFoundResult>(await caller.GetPeer("a", "c", default));
        Assert.IsType<NotFoundResult>(await caller.SendMessage("a", new("c", "link"), default));
    }

    [PostgreSqlFact]
    public async Task ProjectGrants_ReachTheMcpTools_ThroughTheSameDecision()
    {
        // #409: alice's agent in "a" reaches bob's project sessions as soon as bob shares the
        // project, a session bob starts there later included, and loses them when he revokes it.
        await using var db = await Setup();
        var project = (await db.Projects.CreateAsync("bob", new CreateProjectRequest("Release", null))).Id;
        var b = (await db.GetSessionAsync("bob", "b"))!;
        b.ProjectId = project;
        await db.UpsertSessionAsync(b);
        var projects = new ProjectSharingService(db.Shares, new FakeEnterpriseLicense(true));
        var caller = Controller(db, "a");

        IEnumerable<ProjectAgentInfo> Listed(IActionResult result)
            => Assert.IsAssignableFrom<IEnumerable<ProjectAgentInfo>>(Assert.IsType<OkObjectResult>(result).Value);

        Assert.DoesNotContain(Listed(await caller.ProjectAgents("a", default)), s => s.Id == "b");
        await projects.ShareAsync("bob", project, ProjectShareSubjectType.User, "alice", ShareRole.Viewer);
        Assert.Contains(Listed(await caller.ProjectAgents("a", default)), s => s.Id == "b" && s.AccessRole == "Viewer");
        var status = Assert.IsType<SharedSessionInfo>(Assert.IsType<OkObjectResult>(await caller.GetPeer("a", "b", default)).Value);
        Assert.Equal("Release", status.SharedViaProject);
        Assert.IsType<NotFoundResult>(await caller.SendMessage("a", new("b", "viewer"), default));
        await AssertManagementDenied(caller);

        await projects.ShareAsync("bob", project, ProjectShareSubjectType.User, "alice", ShareRole.Collaborator);
        Assert.IsType<OkObjectResult>(await caller.SendMessage("a", new("b", "via project"), default));
        Assert.Equal(1L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM cross_owner_message_audit WHERE recipient_session_id = 'b'"));

        await db.UpsertSessionAsync(new SessionRecord
        {
            Id = "later", Owner = "bob", Title = "later", Mode = SessionMode.Interactive,
            ClaudeSessionId = "claude-later", CallbackToken = "callback-later", ProjectId = project
        });
        Assert.Contains(Listed(await caller.ProjectAgents("a", default)), s => s.Id == "later" && s.AccessRole == "Collaborator");
        Assert.IsType<OkObjectResult>(await caller.SendMessage("a", new("later", "new session"), default));

        await projects.UnshareAsync("bob", project, ProjectShareSubjectType.User, "alice");
        Assert.IsType<NotFoundResult>(await caller.GetPeer("a", "b", default));
        Assert.IsType<NotFoundResult>(await caller.SendMessage("a", new("later", "after revocation"), default));
        Assert.DoesNotContain(Listed(await caller.ProjectAgents("a", default)), s => s.Id is "b" or "later");
    }

    private static async Task AssertManagementDenied(InternalController controller)
    {
        Assert.IsType<NotFoundResult>(await controller.DeletePeer("a", "b", default));
        Assert.IsType<NotFoundResult>(await controller.ConvertPeer("a", "b", new(), default));
        Assert.IsType<NotFoundResult>(await controller.PeerAccountStatus("a", "b", default));
        Assert.IsType<NotFoundResult>(await controller.SwitchPeerCredential("a", "b", new("foreign"), default));
    }

    private static async Task<PostgresSharingDatabase> Setup()
    {
        var db = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await db.AddSessionAsync("alice", "a");
        await db.AddSessionAsync("bob", "b");
        await db.AddUserAsync("alice");
        await db.AddUserAsync("bob");
        await new PostgresSessionMessageStore(db.Configuration).InitializeAsync();
        await new PostgresSecretRequestStore(db.Configuration).InitializeAsync();
        return db;
    }

    private static InternalController Controller(PostgresSharingDatabase db, string id, bool licensed = true, bool ownedFleet = false)
    {
        var service = DispatchProxy.Create<ISessionService, EmptySessionService>();
        if (ownedFleet) ((EmptySessionService)(object)service).Store = db.Sessions;
        var controller = new InternalController(db.Sessions, [], service, null!, [], [], null!, null!,
            messages: new PostgresSessionMessageStore(db.Configuration),
            secretRequests: new PostgresSecretRequestStore(db.Configuration),
            access: new SessionAccessService(db.Shares, new FakeEnterpriseLicense(licensed)),
            sharing: new SessionSharingService(db.Shares, new FakeEnterpriseLicense(licensed), null))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Headers["X-Agent-Token"] = $"callback-{id}";
        return controller;
    }

    public class EmptySessionService : DispatchProxy
    {
        public PostgresSessionStore? Store { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(ISessionService.ListSessionsAsync) => List((string)args![0]!),
            nameof(ISessionService.GetSessionAsync) when Store is not null => Get((string)args![0]!, (string)args[1]!),
            _ => throw new InvalidOperationException($"Unexpected service operation: {method?.Name}")
        };
        private async Task<IReadOnlyList<SessionInfo>> List(string owner)
            => Store is null ? [] : (await Store.ListAsync(owner)).Select(Info).ToList();
        private async Task<SessionInfo?> Get(string owner, string id)
            => await Store!.GetAsync(owner, id) is { } record ? Info(record) : null;
        private static SessionInfo Info(SessionRecord record) => new()
        {
            Id = record.Id, Owner = record.Owner, Title = record.Title, Mode = record.Mode,
            Phase = record.Status, ProjectId = record.ProjectId, ParentSessionId = record.ParentSessionId
        };
    }
}
