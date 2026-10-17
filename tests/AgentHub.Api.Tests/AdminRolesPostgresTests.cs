using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Identity;
using AgentHub.Api.Ee.Usage;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Per-user roles, group roles and the role audit against a real Postgres: the store, the
/// precedence in <see cref="AdminAccess"/> over the real stores, and both admin controllers.
/// Skipped unless AGENTHUB_TEST_POSTGRES_CONNECTION_STRING is set.
/// </summary>
public class AdminRolesPostgresTests
{
    [PostgreSqlFact]
    public async Task RoleStore_RoundTrips_CaseInsensitively_AndReportsThePreviousRole()
    {
        await using var db = await AdminRolesDatabase.CreateAsync();

        Assert.Null(await db.Roles.SetRoleAsync("Alice", "admin"));
        Assert.Equal("admin", await db.Roles.GetRoleAsync("alice"));
        Assert.True(await db.Roles.AnyAdminAsync());

        // Same owner in another case updates the row instead of adding a second one.
        Assert.Equal("admin", await db.Roles.SetRoleAsync("ALICE", "user"));
        var roles = await db.Roles.ListRolesAsync();
        Assert.Single(roles);
        Assert.Equal("user", roles["alice"]);
        Assert.False(await db.Roles.AnyAdminAsync());

        Assert.Equal("user", await db.Roles.SetRoleAsync("alice", null));
        Assert.Null(await db.Roles.GetRoleAsync("alice"));
        await Assert.ThrowsAsync<ArgumentException>(() => db.Roles.SetRoleAsync("alice", "root"));
    }

    [PostgreSqlFact]
    public async Task Audit_ListsNewestFirst()
    {
        await using var db = await AdminRolesDatabase.CreateAsync();
        await db.Roles.AuditAsync("alice", "user", "bob", null, "admin");
        await db.Roles.AuditAsync("alice", "group", "leads", "admin", null);

        var audit = await db.Roles.ListAuditAsync(10);

        Assert.Equal(["leads", "bob"], audit.Select(a => a.Subject));
        Assert.Equal(("group", "admin"), (audit[0].SubjectKind, audit[0].OldRole));
        Assert.Null(audit[0].NewRole);
        Assert.Equal("alice", audit[1].Actor);
    }

    [PostgreSqlFact]
    public async Task Lock_SerializesRoleChanges()
    {
        await using var db = await AdminRolesDatabase.CreateAsync();
        var first = await db.Roles.LockAsync();
        var second = db.Roles.LockAsync();
        await Task.Delay(200);
        Assert.False(second.IsCompleted);

        await first.DisposeAsync();
        await using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [PostgreSqlFact]
    public async Task GroupProvider_AdminGroupWinsOverUserGroup_AndNeedsALicense()
    {
        await using var db = await AdminRolesDatabase.CreateAsync();
        await db.Groups.ReplaceGroupsAsync("alice", ["leads", "staff"]);
        await db.Groups.ReplaceGroupsAsync("bob", ["staff"]);
        await db.Groups.SetGroupRoleAsync("leads", "admin");
        await db.Groups.SetGroupRoleAsync("staff", "user");

        var provider = new GroupRoleAdminProvider(new FakeEnterpriseLicense(true), db.Groups);
        var alice = await provider.GetGroupRoleAsync("alice");
        Assert.Equal("admin", alice!.Role);
        Assert.Equal(["leads"], alice.Groups);
        Assert.Equal("user", (await provider.GetGroupRoleAsync("bob"))!.Role);
        Assert.Null(await provider.GetGroupRoleAsync("carol"));

        var unlicensed = new GroupRoleAdminProvider(new FakeEnterpriseLicense(false), db.Groups);
        Assert.Null(await unlicensed.GetGroupRoleAsync("alice"));
        Assert.False(await unlicensed.HasAdminMappingsAsync());
    }

    [PostgreSqlFact]
    public async Task SetRole_PromotesAKnownUser_EndsBootstrap_AndIsAudited()
    {
        await using var db = await AdminRolesDatabase.CreateAsync();
        await db.SignInAsync("alice", "bob");
        var access = db.Access("");
        var controller = db.RolesController(access, "alice");

        var result = await controller.SetRole("bob", new("admin"), default);

        var body = Assert.IsType<AdminRolesController.RolesOverview>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(body.Bootstrap);
        Assert.Equal(("admin", RoleSources.User), Row(body, "bob"));
        Assert.Equal(("user", RoleSources.Default), Row(body, "alice"));
        Assert.False(await access.IsAdminAsync("alice")); // takes effect on the next request
        var entry = Assert.Single(await db.Roles.ListAuditAsync(10));
        Assert.Equal(("alice", "user", "bob", "admin"), (entry.Actor, entry.SubjectKind, entry.Subject, entry.NewRole));
        Assert.Null(entry.OldRole);
    }

    [PostgreSqlFact]
    public async Task SetRole_RefusesToRemoveTheLastAdmin_IncludingYourself()
    {
        await using var db = await AdminRolesDatabase.CreateAsync();
        await db.SignInAsync("alice", "bob");
        await db.Roles.SetRoleAsync("alice", "admin");
        var access = db.Access("");

        var result = await db.RolesController(access, "alice").SetRole("alice", new("user"), default);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal("admin", await db.Roles.GetRoleAsync("alice")); // rolled back
        Assert.Empty(await db.Roles.ListAuditAsync(10));

        // With a second admin in place, demoting yourself is fine.
        await db.Roles.SetRoleAsync("bob", "admin");
        Assert.IsType<OkObjectResult>(await db.RolesController(access, "alice").SetRole("alice", new("user"), default));
        Assert.False(await access.IsAdminAsync("alice"));
    }

    [PostgreSqlFact]
    public async Task SetRole_RefusesValuesAdmins_UnknownUsers_AndNonAdmins()
    {
        await using var db = await AdminRolesDatabase.CreateAsync();
        await db.SignInAsync("root", "bob");
        var access = db.Access("root");

        Assert.IsType<ConflictObjectResult>(await db.RolesController(access, "root").SetRole("ROOT", new("user"), default));
        Assert.IsType<NotFoundObjectResult>(await db.RolesController(access, "root").SetRole("ghost", new("admin"), default));
        Assert.IsType<BadRequestObjectResult>(await db.RolesController(access, "root").SetRole("bob", new("owner"), default));
        Assert.IsType<ForbidResult>(await db.RolesController(access, "bob").SetRole("bob", new("admin"), default));
        Assert.IsType<ForbidResult>(await db.RolesController(access, "bob").List(default));
        Assert.Empty(await db.Roles.ListAuditAsync(10));
    }

    [PostgreSqlFact]
    public async Task List_ShowsEachSource()
    {
        await using var db = await AdminRolesDatabase.CreateAsync();
        await db.SignInAsync("root", "alice", "bob", "carol");
        await db.Groups.ReplaceGroupsAsync("alice", ["leads"]);
        await db.Groups.ReplaceGroupsAsync("carol", ["staff"]);
        await db.Groups.SetGroupRoleAsync("leads", "admin");
        await db.Groups.SetGroupRoleAsync("staff", "user");
        await db.Roles.SetRoleAsync("bob", "admin");
        var access = db.Access("root", licensed: true);

        var body = Assert.IsType<AdminRolesController.RolesOverview>(
            Assert.IsType<OkObjectResult>(await db.RolesController(access, "root").List(default)).Value);

        Assert.Equal(["root"], body.ValuesAdmins);
        Assert.True(body.GroupMapping);
        Assert.Equal(("admin", RoleSources.Values), Row(body, "root"));
        Assert.Equal(("admin", RoleSources.Group), Row(body, "alice"));
        Assert.Equal(["leads"], body.Users.Single(u => u.Owner == "alice").Groups);
        Assert.Equal(("admin", RoleSources.User), Row(body, "bob"));
        Assert.Equal(("user", RoleSources.Group), Row(body, "carol"));
    }

    [PostgreSqlFact]
    public async Task GroupRole_RefusesToRemoveTheLastAdmin_AndAuditsChanges()
    {
        await using var db = await AdminRolesDatabase.CreateAsync();
        await db.SignInAsync("alice", "bob");
        await db.Groups.ReplaceGroupsAsync("alice", ["leads"]);
        await db.Groups.SetGroupRoleAsync("leads", "admin");
        await db.Groups.SetGroupRoleAsync("ops", "admin"); // mapped, but nobody is a member
        var access = db.Access("", licensed: true);
        var controller = db.GroupController(access, "alice");

        // alice is the only admin, through "leads"; "ops" keeps bootstrap off.
        var refused = await controller.SetGroupRole("leads", new("user"), default);
        Assert.IsType<ConflictObjectResult>(refused);
        Assert.Equal("admin", await db.Groups.GetGroupRoleAsync("leads"));

        // Free entry: a group nobody has signed in with yet, typed the way Keycloak sends paths.
        Assert.IsType<OkObjectResult>(await controller.SetGroupRole(" /staff ", new("user"), default));
        Assert.Equal("user", await db.Groups.GetGroupRoleAsync("staff"));
        // A subgroup path arrives with its slash still encoded in the route value.
        Assert.IsType<OkObjectResult>(await controller.SetGroupRole("%2Fteam%2Fsub", new("user"), default));
        Assert.Equal("user", await db.Groups.GetGroupRoleAsync("team/sub"));
        var entry = (await db.Roles.ListAuditAsync(10)).Single(a => a.Subject == "staff");
        Assert.Equal(("alice", "group", "staff", "user"), (entry.Actor, entry.SubjectKind, entry.Subject, entry.NewRole));
        Assert.Null(entry.OldRole);
    }

    private static (string, string) Row(AdminRolesController.RolesOverview body, string owner)
    {
        var row = body.Users.Single(u => u.Owner == owner);
        return (row.Role, row.Source);
    }
}

internal sealed class AdminRolesDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;
    private readonly IConfiguration _configuration;

    private AdminRolesDatabase(string baseConnectionString, string schema, IConfiguration configuration)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        _configuration = configuration;
        Roles = new UserRoleStore(configuration);
        Groups = new UserGroupStore(configuration);
        Users = new UserDirectory(configuration);
        Limits = new UsageLimitStore(configuration);
    }

    public UserRoleStore Roles { get; }
    public UserGroupStore Groups { get; }
    public UserDirectory Users { get; }
    public UsageLimitStore Limits { get; }

    public static async Task<AdminRolesDatabase> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");

        var schema = $"roles_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = builder.ConnectionString
            })
            .Build();

        var db = new AdminRolesDatabase(baseConnectionString, schema, configuration);
        try
        {
            await db.Roles.InitializeAsync();
            await db.Groups.InitializeAsync();
            await db.Users.InitializeAsync();
            await db.Limits.InitializeAsync();
            return db;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    public async Task SignInAsync(params string[] owners)
    {
        foreach (var owner in owners) await Users.RecordLoginAsync(owner, $"{owner}@example.com", owner);
    }

    public AdminAccess Access(string admins, bool licensed = false)
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Ee:Admins", admins) })
            .Build();
        return new AdminAccess(cfg, NullLogger<AdminAccess>.Instance,
            [new GroupRoleAdminProvider(new FakeEnterpriseLicense(licensed), Groups)], Roles);
    }

    public AdminRolesController RolesController(AdminAccess access, string caller)
        => WithCaller(new AdminRolesController(access, Roles, Users, new FakeEnterpriseLicense(true)), caller);

    public UsageAdminController GroupController(AdminAccess access, string caller)
        => WithCaller(new UsageAdminController(new FakeEnterpriseLicense(true), access, Limits, Groups, Roles, Users), caller);

    private static T WithCaller<T>(T controller, string caller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim("preferred_username", caller) }, "test"))
            }
        };
        return controller;
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(_baseConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }
}
