using System.Reflection;
using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionOrganizationPostgresTests
{
    [PostgreSqlFact]
    public async Task OrdersPersistPerOwner_AndForeignOrMissingSessionsRollBackTheWholeChange()
    {
        await using var db = await PostgresSharingDatabase.CreateAsync();
        var order = new PostgresSessionOrderStore(db.Configuration); await order.InitializeAsync();
        await Add(db, "a", "alice"); await Add(db, "b", "alice"); await Add(db, "c", "bob");
        Assert.True(await order.SetAsync("alice", ["b", "a"]));
        Assert.True(await order.SetAsync("bob", ["c"]));
        Assert.Equal(["b", "a"], await new PostgresSessionOrderStore(db.Configuration).GetAsync("alice"));
        Assert.False(await order.SetAsync("alice", ["a", "c"]));
        Assert.False(await order.SetAsync("alice", ["missing"]));
        Assert.Equal(["b", "a"], await order.GetAsync("alice"));
        Assert.Equal(["c"], await order.GetAsync("bob"));
        await db.Sessions.DeleteAsync("b");
        Assert.Equal(["a"], await order.GetAsync("alice"));
    }

    [PostgreSqlFact]
    public async Task MoveAndOrderEndpointsRefuseForeignSessionAndForeignProject()
    {
        await using var db = await PostgresSharingDatabase.CreateAsync();
        var order = new PostgresSessionOrderStore(db.Configuration); await order.InitializeAsync();
        var projects = new PostgresProjectStore(db.Configuration); await projects.InitializeAsync();
        var own = await projects.CreateAsync("alice", new("Own", null));
        var foreign = await projects.CreateAsync("bob", new("Foreign", null));
        await Add(db, "own", "alice"); await Add(db, "foreign", "bob");
        var controller = new SessionOrganizationController(order, projects, db.Sessions, DispatchProxy.Create<ISessionService, MustNotCall>()) {
            ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "test")) } }
        };
        Assert.IsType<NotFoundResult>((await controller.Move("foreign", new(own.Id, true), default)).Result);
        Assert.IsType<NotFoundResult>((await controller.Move("own", new(foreign.Id, true), default)).Result);
        Assert.IsType<NotFoundResult>((await controller.Move("missing", new(own.Id, true), default)).Result);
        Assert.IsType<ConflictObjectResult>((await controller.Move("own", new(own.Id), default)).Result);
        Assert.IsType<NotFoundResult>(await controller.SetOrder(new(["foreign"]), default));
        Assert.IsType<BadRequestObjectResult>(await controller.SetOrder(new(["own", "own"]), default));
        Assert.Empty(await order.GetAsync("alice"));
        Assert.Null((await db.Sessions.GetAsync("alice", "own"))!.ProjectId);
    }

    private static Task Add(PostgresSharingDatabase db, string id, string owner) => db.Sessions.UpsertAsync(new SessionRecord {
        Id = id, Owner = owner, CallbackToken = "synthetic-token", Title = id, Status = "Paused", Mode = SessionMode.Interactive
    });
    public class MustNotCall : DispatchProxy {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("Unauthorized requests must not reach the session service.");
    }
}
