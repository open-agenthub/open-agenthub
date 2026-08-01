using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibraryAccessServiceTests
{
    private static (LibraryAccessService Access, InMemoryMcpServerStore Mcp, InMemorySkillStore Skills, InMemoryLibraryShareStore Shares)
        Build(bool licensed)
    {
        var mcp = new InMemoryMcpServerStore();
        var skills = new InMemorySkillStore();
        var shares = new InMemoryLibraryShareStore();
        var access = new LibraryAccessService(mcp, skills, shares, new FakeEnterpriseLicense(licensed));
        return (access, mcp, skills, shares);
    }

    [Fact]
    public async Task WithoutLicense_OnlyOwnItemsAreVisible()
    {
        var (access, mcp, skills, shares) = Build(licensed: false);
        mcp.Add("alice", "own-server");
        var foreign = mcp.Add("bob", "shared-server");
        await shares.SetSharesAsync(LibraryItemTypes.Mcp, foreign.Id, all: true, null, null, "bob");
        skills.Add("alice", "own-skill");
        var foreignSkill = skills.Add("bob", "shared-skill");
        await shares.SetSharesAsync(LibraryItemTypes.Skill, foreignSkill.Id, all: true, null, null, "bob");

        Assert.Equal(["own-server"], (await access.ListMcpServersAsync("alice")).Select(s => s.Name));
        Assert.Equal(["own-skill"], (await access.ListSkillsAsync("alice")).Select(s => s.Name));
        Assert.Null(await access.GetSkillAsync("alice", foreignSkill.Id));
    }

    [Fact]
    public async Task WithLicense_SharedItemsAppear_ViaUserGroupAndAll()
    {
        var (access, mcp, _, shares) = Build(licensed: true);
        shares.KnownUsers.Add("alice");
        var viaAll = mcp.Add("bob", "via-all");
        var viaUser = mcp.Add("bob", "via-user");
        var viaGroup = mcp.Add("bob", "via-group");
        var notShared = mcp.Add("bob", "not-shared");

        await shares.SetSharesAsync(LibraryItemTypes.Mcp, viaAll.Id, all: true, null, null, "bob");
        await shares.SetSharesAsync(LibraryItemTypes.Mcp, viaUser.Id, all: false, ["alice"], null, "bob");
        var group = await shares.CreateGroupAsync("devs");
        await shares.SetGroupMembersAsync(group.Id, ["alice"]);
        await shares.SetSharesAsync(LibraryItemTypes.Mcp, viaGroup.Id, all: false, null, [group.Id], "bob");

        var names = (await access.ListMcpServersAsync("alice")).Select(s => s.Name).ToList();
        Assert.Equal(["via-all", "via-group", "via-user"], names);
        Assert.DoesNotContain(notShared.Name, names);
    }

    [Fact]
    public async Task ResolveStrict_ThrowsForInaccessibleIds()
    {
        var (access, mcp, _, _) = Build(licensed: true);
        var own = mcp.Add("alice", "own");
        var foreign = mcp.Add("bob", "foreign");

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            access.ResolveMcpServersAsync("alice", [own.Id, foreign.Id, "missing"], strict: true));
        Assert.Contains(foreign.Id, error.Message);
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public async Task ResolveLenient_DropsInaccessibleIds()
    {
        var (access, mcp, _, shares) = Build(licensed: false);
        var own = mcp.Add("alice", "own");
        var foreign = mcp.Add("bob", "foreign");
        // Was shared once (id stored on the session) but the license lapsed.
        await shares.SetSharesAsync(LibraryItemTypes.Mcp, foreign.Id, all: true, null, null, "bob");

        var resolved = await access.ResolveMcpServersAsync(
            "alice", [own.Id, foreign.Id], strict: false);
        Assert.Equal([own.Id], resolved.Select(r => r.Id));
    }

    [Fact]
    public async Task SkillPayloads_OwnSkillWinsNameConflict()
    {
        var (access, _, skills, shares) = Build(licensed: true);
        skills.Add("alice", "review", "# my own review skill");
        var foreign = skills.Add("bob", "review", "# bobs review skill");
        var extra = skills.Add("bob", "deploy", "# deploy skill");
        await shares.SetSharesAsync(LibraryItemTypes.Skill, foreign.Id, all: true, null, null, "bob");
        await shares.SetSharesAsync(LibraryItemTypes.Skill, extra.Id, all: true, null, null, "bob");

        var payloads = await access.ListSkillPayloadsAsync("alice", null);
        Assert.Equal(["deploy", "review"], payloads.Select(p => p.Name));
        Assert.Equal("# my own review skill", payloads.Single(p => p.Name == "review").Content);
    }
}
