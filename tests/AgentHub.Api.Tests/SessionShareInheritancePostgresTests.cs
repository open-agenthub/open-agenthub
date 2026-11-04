using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// #407 and #410 against the real store: a direct share puts the session and, live, all its
/// descendants into the recipient's list and opens them with the strongest role that reaches
/// them; a link opens its own session only and lists nothing.
/// </summary>
public class SessionShareInheritancePostgresTests
{
    private static async Task<(PostgresSharingDatabase Database, SessionAccessService Access, SessionSharingService Sharing)> CreateAsync()
    {
        var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddUserAsync("alice");
        await database.AddUserAsync("bob");
        await database.AddUserAsync("carol");
        var license = new FakeEnterpriseLicense(true);
        return (database, new SessionAccessService(database.Shares, license),
            new SessionSharingService(database.Shares, license, "https://hub.example.com"));
    }

    private static Task AddChildAsync(PostgresSharingDatabase database, string owner, string id, string? parent)
        => database.UpsertSessionAsync(new SessionRecord
        {
            Id = id,
            Owner = owner,
            Title = id,
            Mode = SessionMode.Interactive,
            ClaudeSessionId = $"claude-{id}",
            CallbackToken = $"callback-{id}",
            ParentSessionId = parent
        });

    [PostgreSqlFact]
    public async Task Child_InheritsTheParentsRole_AndLosesItLiveWhenTheShareIsRevoked()
    {
        var (database, access, sharing) = await CreateAsync();
        await using var _ = database;
        await AddChildAsync(database, "alice", "parent", null);
        await AddChildAsync(database, "alice", "child", "parent");

        Assert.Null(await access.ResolveUserAsync("bob", "child"));
        await sharing.ShareWithUserAsync("alice", "parent", "bob", ShareRole.Viewer);

        var viewer = await access.ResolveUserAsync("bob", "child");
        Assert.NotNull(viewer);
        Assert.Equal(SessionAccessLevel.Viewer, viewer.Level);
        Assert.Equal("parent", viewer.InheritedFrom);
        Assert.Equal("alice", viewer.SharedBy);
        Assert.Null(await access.ResolveUserAsync("carol", "child"));

        // A role change on the parent reaches the child without touching it.
        await sharing.ShareWithUserAsync("alice", "parent", "bob", ShareRole.Collaborator);
        Assert.Equal(SessionAccessLevel.Collaborator, (await access.ResolveUserAsync("bob", "child"))!.Level);

        await sharing.UnshareUserAsync("alice", "parent", "bob");
        Assert.Null(await access.ResolveUserAsync("bob", "child"));
        Assert.Null(await access.ResolveUserAsync("bob", "parent"));
        Assert.Empty(await sharing.ListSharedWithAsync("bob"));
        // Live inheritance copies nothing: the child never had a row of its own.
        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM session_shares"));
    }

    [PostgreSqlFact]
    public async Task StrongestGrantWins_WhereverItIsAttached()
    {
        var (database, access, _) = await CreateAsync();
        await using var __ = database;
        var sharing = new SessionSharingService(database.Shares, new FakeEnterpriseLicense(true), null);
        await AddChildAsync(database, "alice", "root", null);
        await AddChildAsync(database, "alice", "mid", "root");
        await AddChildAsync(database, "alice", "leaf", "mid");

        // Viewer on the root, Collaborator on the leaf: the leaf's own grant is stronger.
        await sharing.ShareWithUserAsync("alice", "root", "bob", ShareRole.Viewer);
        await sharing.ShareWithUserAsync("alice", "leaf", "bob", ShareRole.Collaborator);
        var leaf = (await access.ResolveUserAsync("bob", "leaf"))!;
        Assert.Equal(SessionAccessLevel.Collaborator, leaf.Level);
        Assert.Null(leaf.InheritedFrom);
        Assert.Equal(SessionAccessLevel.Viewer, (await access.ResolveUserAsync("bob", "mid"))!.Level);

        // Collaborator on the middle, Viewer on the leaf: a narrower grant never narrows access.
        await sharing.ShareWithUserAsync("alice", "mid", "bob", ShareRole.Collaborator);
        await sharing.ShareWithUserAsync("alice", "leaf", "bob", ShareRole.Viewer);
        leaf = (await access.ResolveUserAsync("bob", "leaf"))!;
        Assert.Equal(SessionAccessLevel.Collaborator, leaf.Level);
        Assert.Equal("mid", leaf.InheritedFrom);

        // The list makes exactly the same decision as the single lookup.
        var listed = (await sharing.ListSharedWithAsync("bob")).ToDictionary(session => session.Id);
        Assert.Equal(["leaf", "mid", "root"], listed.Keys.Order());
        Assert.Equal("Collaborator", listed["leaf"].AccessRole);
        Assert.Equal("mid", listed["leaf"].InheritedFrom);
        Assert.Equal("Collaborator", listed["mid"].AccessRole);
        Assert.Null(listed["mid"].InheritedFrom);
        Assert.Equal("Viewer", listed["root"].AccessRole);
        Assert.Equal("mid", listed["leaf"].ParentSessionId);
        Assert.Equal("root", listed["mid"].ParentSessionId);
        Assert.Null(listed["root"].ParentSessionId);
    }

    [PostgreSqlFact]
    public async Task DeepChain_IsInheritedToTheBottom_AndOnlyBelowTheSharedSession()
    {
        var (database, access, sharing) = await CreateAsync();
        await using var _ = database;
        await AddChildAsync(database, "alice", "level-0", null);
        for (var level = 1; level <= 20; level++)
            await AddChildAsync(database, "alice", $"level-{level}", $"level-{level - 1}");

        await sharing.ShareWithUserAsync("alice", "level-5", "bob", ShareRole.Collaborator);

        var bottom = (await access.ResolveUserAsync("bob", "level-20"))!;
        Assert.Equal(SessionAccessLevel.Collaborator, bottom.Level);
        Assert.Equal("level-5", bottom.InheritedFrom);
        // Access flows down the chain, never up.
        Assert.Null(await access.ResolveUserAsync("bob", "level-4"));
        Assert.Null(await access.ResolveUserAsync("bob", "level-0"));

        var listed = await sharing.ListSharedWithAsync("bob");
        Assert.Equal(16, listed.Count);
        Assert.All(listed, session => Assert.Equal("Collaborator", session.AccessRole));
        // The shared session itself is the top of what bob sees, so it names no parent.
        Assert.Null(listed.Single(session => session.Id == "level-5").ParentSessionId);
        Assert.Equal("level-19", listed.Single(session => session.Id == "level-20").ParentSessionId);
    }

    [PostgreSqlFact]
    public async Task SessionOfAnotherOwner_UnderASharedOne_InheritsNothing()
    {
        var (database, access, sharing) = await CreateAsync();
        await using var _ = database;
        await AddChildAsync(database, "alice", "shared", null);
        // Carol names alice's session as her parent; the id is all a creating request needs.
        await AddChildAsync(database, "carol", "planted", "shared");
        await sharing.ShareWithUserAsync("alice", "shared", "bob", ShareRole.Collaborator);

        Assert.Null(await access.ResolveUserAsync("bob", "planted"));
        Assert.Equal(["shared"], (await sharing.ListSharedWithAsync("bob")).Select(session => session.Id));
        Assert.False(await database.Shares.IsSharedAsync("planted"));
    }

    [PostgreSqlFact]
    public async Task Links_StayLinkOnly_ForChildrenAndLists()
    {
        var (database, access, sharing) = await CreateAsync();
        await using var _ = database;
        await AddChildAsync(database, "alice", "parent", null);
        await AddChildAsync(database, "alice", "child", "parent");

        var link = await sharing.CreateLinkAsync("alice", "parent", ShareRole.Collaborator, null);
        var token = link.Url[(link.Url.LastIndexOf('/') + 1)..];

        var opened = (await access.ResolveTokenAsync(token))!;
        Assert.Equal("parent", opened.Session.Id);
        Assert.Null(opened.InheritedFrom);
        // A link adds nothing to anybody's list and opens neither the child nor, by name, the parent.
        Assert.Empty(await sharing.ListSharedWithAsync("bob"));
        Assert.Null(await access.ResolveUserAsync("bob", "parent"));
        Assert.Null(await access.ResolveUserAsync("bob", "child"));
        Assert.True(await database.Shares.IsSharedAsync("parent"));
        Assert.False(await database.Shares.IsSharedAsync("child"));

        // A direct share on the parent does make the child shared, for the sibling-file rule.
        await sharing.ShareWithUserAsync("alice", "parent", "bob", ShareRole.Viewer);
        Assert.True(await database.Shares.IsSharedAsync("child"));
    }

    [PostgreSqlFact]
    public async Task DeletingTheParent_LeavesNoGrant_AndClosesTheChildren()
    {
        var (database, access, sharing) = await CreateAsync();
        await using var _ = database;
        await AddChildAsync(database, "alice", "parent", null);
        await AddChildAsync(database, "alice", "child", "parent");
        await sharing.ShareWithUserAsync("alice", "parent", "bob", ShareRole.Viewer);
        Assert.Equal(2, (await sharing.ListSharedWithAsync("bob")).Count);

        await database.Sessions.DeleteAsync("parent");

        Assert.Equal(0L, await database.ScalarAsync<long>("SELECT count(*) FROM session_shares"));
        Assert.Null(await access.ResolveUserAsync("bob", "child"));
        Assert.Empty(await sharing.ListSharedWithAsync("bob"));
        // The owner keeps the orphaned child.
        Assert.Equal(SessionAccessLevel.Owner, (await access.ResolveUserAsync("alice", "child"))!.Level);
    }

    [PostgreSqlFact]
    public async Task CorruptCycle_Terminates()
    {
        var (database, access, sharing) = await CreateAsync();
        await using var _ = database;
        await AddChildAsync(database, "alice", "a", null);
        await AddChildAsync(database, "alice", "b", "a");
        await database.ExecuteAsync("UPDATE sessions SET parent_session_id = 'b' WHERE id = 'a'");
        await sharing.ShareWithUserAsync("alice", "a", "bob", ShareRole.Viewer);

        Assert.Equal(SessionAccessLevel.Viewer, (await access.ResolveUserAsync("bob", "b"))!.Level);
        Assert.Equal(["a", "b"], (await sharing.ListSharedWithAsync("bob")).Select(session => session.Id).Order());
        Assert.True(await database.Shares.IsSharedAsync("b"));
    }

    [PostgreSqlFact]
    public async Task McpPolicy_OfEveryAncestor_HoldsTheChild()
    {
        var (database, _, sharing) = await CreateAsync();
        await using var __ = database;
        await AddChildAsync(database, "alice", "parent", null);
        await AddChildAsync(database, "alice", "child", "parent");
        await AddChildAsync(database, "carol", "planted", "parent");
        await sharing.SetMcpPolicyAsync("alice", "parent", ["github"], ["mcp__mail__send"]);
        await sharing.SetMcpPolicyAsync("alice", "child", ["browser"], []);

        var child = (await database.Shares.GetMcpPolicyAsync("child"))!;
        Assert.Equal(["browser", "github"], child.BlockedServers.Order());
        Assert.Equal(["mcp__mail__send"], child.BlockedTools);
        var parent = (await database.Shares.GetMcpPolicyAsync("parent"))!;
        Assert.Equal(["github"], parent.BlockedServers);
        Assert.Null(await database.Shares.GetMcpPolicyAsync("planted"));
        // The owner's dialog shows the child's own policy, not the inherited blocks.
        Assert.Equal(["browser"], (await sharing.ListAsync("alice", "child")).McpPolicy!.BlockedServers);
    }

    [PostgreSqlFact]
    public async Task OwnerLookup_IsUnchanged_AndAStrangerGetsNothing()
    {
        var (database, access, _) = await CreateAsync();
        await using var __ = database;
        await AddChildAsync(database, "alice", "parent", null);
        await AddChildAsync(database, "alice", "child", "parent");

        var owner = (await access.ResolveUserAsync("alice", "child"))!;
        Assert.Equal(SessionAccessLevel.Owner, owner.Level);
        Assert.Null(owner.SharedBy);
        Assert.Equal("parent", owner.Session.ParentSessionId);
        Assert.Null(await database.Shares.FindUserAccessAsync("bob", "child"));
        Assert.Null(await database.Shares.FindUserAccessAsync("bob", "missing"));
    }
}
