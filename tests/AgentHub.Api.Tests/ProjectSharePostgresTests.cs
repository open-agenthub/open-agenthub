using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// #409 against the real store: a share on a project opens every session in it, existing and
/// future, and their children, live; a session share can only add to it; revoking it, moving a
/// session out, deleting the project or purging an account removes the access with no row left.
/// </summary>
public class ProjectSharePostgresTests
{
    private sealed record Rig(
        PostgresSharingDatabase Database,
        SessionAccessService Access,
        SessionSharingService Sessions,
        ProjectSharingService Projects) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Database.DisposeAsync();

        public Task AddAsync(string owner, string id, string? project, string? parent = null)
            => Database.UpsertSessionAsync(new SessionRecord
            {
                Id = id,
                Owner = owner,
                Title = id,
                Mode = SessionMode.Interactive,
                ClaudeSessionId = $"claude-{id}",
                CallbackToken = $"callback-{id}",
                ProjectId = project,
                ParentSessionId = parent
            });

        public async Task<string> ProjectAsync(string owner, string name)
            => (await Database.Projects.CreateAsync(owner, new CreateProjectRequest(name, null))).Id;

        public async Task<SessionAccessLevel> LevelAsync(string principal, string session)
            => (await Access.ResolveUserAsync(principal, session))?.Level ?? SessionAccessLevel.None;

        public async Task<string[]> ListedAsync(string principal)
            => (await Sessions.ListSharedWithAsync(principal)).Select(session => session.Id).Order().ToArray();
    }

    private static async Task<Rig> CreateAsync()
    {
        var database = await PostgresSharingDatabase.CreateAsync();
        foreach (var user in new[] { "alice", "bob", "carol", "dave" })
            await database.AddUserAsync(user);
        var license = new FakeEnterpriseLicense(true);
        return new Rig(
            database,
            new SessionAccessService(database.Shares, license),
            new SessionSharingService(database.Shares, license, "https://hub.example.com"),
            new ProjectSharingService(database.Shares, license));
    }

    [PostgreSqlFact]
    public async Task ProjectShare_OpensEverySession_IncludingOnesCreatedAfterwards()
    {
        await using var rig = await CreateAsync();
        var project = await rig.ProjectAsync("alice", "Release");
        await rig.AddAsync("alice", "existing", project);
        await rig.AddAsync("alice", "elsewhere", null);

        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "bob", ShareRole.Viewer);
        var opened = await rig.Access.ResolveUserAsync("bob", "existing");
        Assert.Equal(SessionAccessLevel.Viewer, opened!.Level);
        Assert.Equal("Release", opened.SharedViaProject);
        Assert.Null(opened.InheritedFrom);
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", "elsewhere"));
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("carol", "existing"));

        // Nothing is copied: a session created after the share opens on its first request.
        await rig.AddAsync("alice", "later", project);
        Assert.Equal(SessionAccessLevel.Viewer, await rig.LevelAsync("bob", "later"));
        Assert.Equal(["existing", "later"], await rig.ListedAsync("bob"));
        var listed = (await rig.Sessions.ListSharedWithAsync("bob")).Single(session => session.Id == "later");
        Assert.Equal("Release", listed.SharedViaProject);
        Assert.Equal("alice", listed.SharedBy);
        Assert.True(await rig.Database.Shares.IsSharedAsync("later"));
        Assert.False(await rig.Database.Shares.IsSharedAsync("elsewhere"));

        // A role change on the project reaches every session at once.
        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "bob", ShareRole.Collaborator);
        Assert.Equal(SessionAccessLevel.Collaborator, await rig.LevelAsync("bob", "existing"));
        Assert.Equal(SessionAccessLevel.Owner, await rig.LevelAsync("alice", "existing"));
    }

    [PostgreSqlFact]
    public async Task Children_InheritTheProjectShare_ThroughTheAncestorRule()
    {
        await using var rig = await CreateAsync();
        var project = await rig.ProjectAsync("alice", "Release");
        await rig.AddAsync("alice", "parent", project);
        // A child started outside the project still belongs under its parent.
        await rig.AddAsync("alice", "child", null, "parent");
        await rig.AddAsync("alice", "grandchild", project, "child");
        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "bob", ShareRole.Viewer);

        var child = await rig.Access.ResolveUserAsync("bob", "child");
        Assert.Equal(SessionAccessLevel.Viewer, child!.Level);
        Assert.Equal("parent", child.InheritedFrom);
        Assert.Equal("Release", child.SharedViaProject);
        // The grandchild is in the project itself, which is the more specific reason.
        Assert.Null((await rig.Access.ResolveUserAsync("bob", "grandchild"))!.InheritedFrom);

        var list = await rig.Sessions.ListSharedWithAsync("bob");
        Assert.Equal(["child", "grandchild", "parent"], list.Select(session => session.Id).Order());
        Assert.Equal("parent", list.Single(session => session.Id == "child").ParentSessionId);
        Assert.Equal("child", list.Single(session => session.Id == "grandchild").ParentSessionId);
        Assert.True(await rig.Database.Shares.IsSharedAsync("child"));
    }

    [PostgreSqlFact]
    public async Task ASessionShare_CanOnlyAdd_AndTheListAgreesWithTheLookup()
    {
        await using var rig = await CreateAsync();
        var project = await rig.ProjectAsync("alice", "Release");
        await rig.AddAsync("alice", "narrowed", project);
        await rig.AddAsync("alice", "widened", project);
        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "bob", ShareRole.Collaborator);
        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "carol", ShareRole.Viewer);

        // Viewer on the session does not take Collaborator from bob …
        await rig.Sessions.ShareWithUserAsync("alice", "narrowed", "bob", ShareRole.Viewer);
        Assert.Equal(SessionAccessLevel.Collaborator, await rig.LevelAsync("bob", "narrowed"));
        // … and Collaborator on the session gives carol more than the project does.
        await rig.Sessions.ShareWithUserAsync("alice", "widened", "carol", ShareRole.Collaborator);
        Assert.Equal(SessionAccessLevel.Collaborator, await rig.LevelAsync("carol", "widened"));
        Assert.Equal(SessionAccessLevel.Viewer, await rig.LevelAsync("carol", "narrowed"));

        foreach (var principal in new[] { "bob", "carol" })
        {
            foreach (var listed in await rig.Sessions.ListSharedWithAsync(principal))
                Assert.Equal((await rig.LevelAsync(principal, listed.Id)).ToString(), listed.AccessRole);
        }

        // Revoking the project leaves exactly what the session shares give.
        await rig.Projects.UnshareAsync("alice", project, ProjectShareSubjectType.User, "bob");
        Assert.Equal(SessionAccessLevel.Viewer, await rig.LevelAsync("bob", "narrowed"));
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", "widened"));
    }

    [PostgreSqlFact]
    public async Task Revoking_OrMovingOut_ClosesTheSessionsLive()
    {
        await using var rig = await CreateAsync();
        var project = await rig.ProjectAsync("alice", "Release");
        await rig.AddAsync("alice", "one", project);
        await rig.AddAsync("alice", "two", project);
        await rig.AddAsync("alice", "child", null, "two");
        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "bob", ShareRole.Viewer);
        Assert.Equal(["child", "one", "two"], await rig.ListedAsync("bob"));

        await rig.Database.ExecuteAsync("UPDATE sessions SET project_id = NULL WHERE id = 'one'");
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", "one"));
        Assert.False(await rig.Database.Shares.IsSharedAsync("one"));
        Assert.Equal(["child", "two"], await rig.ListedAsync("bob"));

        await rig.Projects.UnshareAsync("alice", project, ProjectShareSubjectType.User, "bob");
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", "two"));
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", "child"));
        Assert.Empty(await rig.ListedAsync("bob"));
        Assert.False(await rig.Database.Shares.IsSharedAsync("child"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            rig.Projects.UnshareAsync("alice", project, ProjectShareSubjectType.User, "bob"));
    }

    [PostgreSqlFact]
    public async Task GroupShare_ReachesTheMembers_AndOnlyTheOwnersGroupsCanBeNamed()
    {
        await using var rig = await CreateAsync();
        var project = await rig.ProjectAsync("alice", "Release");
        await rig.AddAsync("alice", "s1", project);
        await rig.Database.Groups.ReplaceGroupsAsync("alice", ["release-team"]);
        await rig.Database.Groups.ReplaceGroupsAsync("bob", ["release-team"]);
        await rig.Database.Groups.ReplaceGroupsAsync("carol", ["other-team"]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.Group, "other-team", ShareRole.Viewer));
        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.Group, "release-team", ShareRole.Collaborator);

        Assert.Equal(SessionAccessLevel.Collaborator, await rig.LevelAsync("bob", "s1"));
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("carol", "s1"));
        Assert.Equal(["s1"], await rig.ListedAsync("bob"));

        // Memberships come from the last sign-in; leaving the group closes the project.
        await rig.Database.Groups.ReplaceGroupsAsync("bob", []);
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", "s1"));
        Assert.Empty(await rig.ListedAsync("bob"));
        // The owner, a member of the group, stays owner.
        Assert.Equal(SessionAccessLevel.Owner, await rig.LevelAsync("alice", "s1"));
        Assert.Empty(await rig.ListedAsync("alice"));
    }

    [PostgreSqlFact]
    public async Task OnlyTheOwnersSessionsInTheProject_AreReached()
    {
        await using var rig = await CreateAsync();
        var project = await rig.ProjectAsync("alice", "Release");
        await rig.AddAsync("alice", "alices", project);
        // project_id on a session is whatever its request named; carol's naming alice's project
        // must not show carol's session to alice's recipients.
        await rig.AddAsync("carol", "planted", project);
        await rig.AddAsync("carol", "planted-child", null, "alices");
        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "bob", ShareRole.Viewer);

        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", "planted"));
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", "planted-child"));
        Assert.Equal(["alices"], await rig.ListedAsync("bob"));
        Assert.False(await rig.Database.Shares.IsSharedAsync("planted"));
    }

    [PostgreSqlFact]
    public async Task ManagingAProject_IsTheOwnersAlone()
    {
        await using var rig = await CreateAsync();
        var project = await rig.ProjectAsync("alice", "Release");
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            rig.Projects.ShareAsync("carol", project, ProjectShareSubjectType.User, "bob", ShareRole.Viewer));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Projects.ListAsync("carol", project));
        await Assert.ThrowsAsync<UnknownRecipientException>(() =>
            rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "nobody", ShareRole.Viewer));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "alice", ShareRole.Viewer));

        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "bob", ShareRole.Viewer);
        var listed = Assert.Single(await rig.Projects.ListAsync("alice", project));
        Assert.Equal(("bob", ShareRole.Viewer), (listed.Subject, listed.Role));

        // The session dialog shows the project's shares beside its own.
        await rig.AddAsync("alice", "s1", project);
        var overview = await rig.Sessions.ListAsync("alice", "s1");
        Assert.Equal("bob", Assert.Single(overview.ProjectShares!).Subject);
    }

    [PostgreSqlFact]
    public async Task DeletingTheProject_CascadesItsShares_AndARecreatedIdInheritsNothing()
    {
        await using var rig = await CreateAsync();
        var project = await rig.ProjectAsync("alice", "Release");
        await rig.AddAsync("alice", "s1", project);
        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "bob", ShareRole.Viewer);

        Assert.True(await rig.Database.Projects.DeleteAsync("alice", project));
        Assert.Equal(0L, await rig.Database.ScalarAsync<long>("SELECT count(*) FROM project_shares"));
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", "s1"));

        // Even a raw delete, which skips the store, takes the shares with it.
        var second = await rig.ProjectAsync("alice", "Second");
        await rig.Projects.ShareAsync("alice", second, ProjectShareSubjectType.User, "bob", ShareRole.Viewer);
        await rig.Database.ExecuteAsync("DELETE FROM projects WHERE id = @id", new NpgsqlParameter("id", second));
        await rig.Database.ExecuteAsync(
            "INSERT INTO projects (id, owner, name) VALUES (@id, 'alice', 'Reborn')", new NpgsqlParameter("id", second));
        await rig.AddAsync("alice", "s2", second);
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", "s2"));
    }

    [PostgreSqlFact]
    public async Task AccountPurge_RemovesTheSharesTheAccountGaveAndReceived()
    {
        await using var rig = await CreateAsync();
        var alices = await rig.ProjectAsync("alice", "Release");
        var carols = await rig.ProjectAsync("carol", "Ops");
        await rig.Projects.ShareAsync("alice", alices, ProjectShareSubjectType.User, "bob", ShareRole.Viewer);
        await rig.Projects.ShareAsync("carol", carols, ProjectShareSubjectType.User, "alice", ShareRole.Viewer);
        await rig.Projects.ShareAsync("carol", carols, ProjectShareSubjectType.User, "bob", ShareRole.Viewer);

        var purge = new AccountPurgeStore(rig.Database.Configuration, NullLogger<AccountPurgeStore>.Instance);
        await purge.PurgeAsync("alice", await purge.ListSessionIdsAsync("alice"));

        Assert.Equal(0L, await rig.Database.ScalarAsync<long>(
            "SELECT count(*) FROM project_shares WHERE subject = 'alice' OR project_id = @p",
            new NpgsqlParameter("p", alices)));
        // carol's share with bob is not alice's to remove.
        Assert.Equal(1L, await rig.Database.ScalarAsync<long>("SELECT count(*) FROM project_shares"));
    }

    [PostgreSqlFact]
    public async Task TheWalk_IsBoundedByMaxHops_AndACycleTerminates()
    {
        await using var rig = await CreateAsync();
        var project = await rig.ProjectAsync("alice", "Deep");
        await rig.AddAsync("alice", "d0", project);
        var hops = AgentHub.Api.Services.SessionDescent.MaxHops;
        for (var depth = 1; depth <= hops + 2; depth++)
            await rig.AddAsync("alice", $"d{depth}", null, $"d{depth - 1}");
        await rig.Projects.ShareAsync("alice", project, ProjectShareSubjectType.User, "bob", ShareRole.Viewer);

        Assert.Equal(SessionAccessLevel.Viewer, await rig.LevelAsync("bob", $"d{hops}"));
        Assert.Equal(SessionAccessLevel.None, await rig.LevelAsync("bob", $"d{hops + 1}"));
        var listed = await rig.ListedAsync("bob");
        Assert.Contains($"d{hops}", listed);
        Assert.DoesNotContain($"d{hops + 1}", listed);

        var cyclic = await rig.ProjectAsync("alice", "Cycle");
        await rig.AddAsync("alice", "a", cyclic, "b");
        await rig.AddAsync("alice", "b", cyclic, "a");
        await rig.Projects.ShareAsync("alice", cyclic, ProjectShareSubjectType.User, "carol", ShareRole.Viewer);
        Assert.Equal(SessionAccessLevel.Viewer, await rig.LevelAsync("carol", "b"));
        Assert.Equal(["a", "b"], await rig.ListedAsync("carol"));
        Assert.True(await rig.Database.Shares.IsSharedAsync("a"));
    }
}
