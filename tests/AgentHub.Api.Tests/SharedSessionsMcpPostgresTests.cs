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
        return db;
    }

    private static InternalController Controller(PostgresSharingDatabase db, string id, bool licensed = true)
    {
        var service = DispatchProxy.Create<ISessionService, EmptySessionService>();
        var controller = new InternalController(db.Sessions, [], service, null!, [], [], null!, null!,
            messages: new PostgresSessionMessageStore(db.Configuration),
            access: new SessionAccessService(db.Shares, new FakeEnterpriseLicense(licensed)),
            sharing: new SessionSharingService(db.Shares, new FakeEnterpriseLicense(licensed), null))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Headers["X-Agent-Token"] = $"callback-{id}";
        return controller;
    }

    public class EmptySessionService : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == nameof(ISessionService.ListSessionsAsync)
                ? Task.FromResult<IReadOnlyList<SessionInfo>>([])
                : throw new InvalidOperationException($"Unexpected service operation: {method?.Name}");
    }
}
