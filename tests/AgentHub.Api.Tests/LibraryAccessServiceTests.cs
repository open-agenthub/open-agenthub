using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibraryAccessServiceTests
{
    private static (LibraryAccessService Access, InMemoryMcpServerStore Mcp, InMemorySkillStore Skills, FakeLibraryShareReader Shares)
        Build(bool licensed, FakeLibraryShareReader? shares = null)
    {
        var mcp = new InMemoryMcpServerStore();
        var skills = new InMemorySkillStore();
        shares ??= new FakeLibraryShareReader();
        var access = new LibraryAccessService(mcp, skills, shares, new FakeEnterpriseLicense(licensed));
        return (access, mcp, skills, shares);
    }

    private static (LibraryAccessService Access, InMemoryMcpServerStore Mcp, InMemorySkillStore Skills, InMemoryLibraryShareStore Shares)
        BuildWithShareStore(bool licensed)
    {
        var mcp = new InMemoryMcpServerStore();
        var skills = new InMemorySkillStore();
        var shares = new InMemoryLibraryShareStore();
        var access = new LibraryAccessService(mcp, skills, shares, new FakeEnterpriseLicense(licensed));
        return (access, mcp, skills, shares);
    }

    [Fact]
    public async Task WithoutLicense_OwnAndOrgAreVisible_ForeignPersonalHidden()
    {
        var (_, mcp, skills, _) = Build(licensed: false);
        mcp.Add("alice", "own-server");
        mcp.Add(McpServerRecord.OrgOwner, "org-server");
        var foreign = mcp.Add("bob", "bob-server");
        skills.Add("alice", "own-skill");
        var foreignSkill = skills.Add("bob", "shared-skill");

        var shares = new FakeLibraryShareReader { Ids = [foreign.Id, foreignSkill.Id] };
        var accessWithShares = new LibraryAccessService(mcp, skills, shares, new FakeEnterpriseLicense(false));

        var mcpNames = (await accessWithShares.ListMcpServersAsync("alice")).Select(s => s.Name).ToList();
        Assert.Equal(["org-server", "own-server"], mcpNames);
        Assert.DoesNotContain("bob-server", mcpNames);
        Assert.Equal(["own-skill"], (await accessWithShares.ListSkillsAsync("alice")).Select(s => s.Name));
        Assert.Null(await accessWithShares.GetSkillAsync("alice", foreignSkill.Id));
    }

    [Fact]
    public async Task WithLicense_OrgRequiresShare_SharedPersonalVisible()
    {
        var (access, mcp, _, shares) = Build(licensed: true);
        mcp.Add("alice", "own-server");
        var orgShared = mcp.Add(McpServerRecord.OrgOwner, "org-shared");
        var orgHidden = mcp.Add(McpServerRecord.OrgOwner, "org-hidden");
        var viaShare = mcp.Add("bob", "via-share");
        var notShared = mcp.Add("bob", "not-shared");

        shares.Ids = [orgShared.Id, viaShare.Id];

        var names = (await access.ListMcpServersAsync("alice")).Select(s => s.Name).ToList();
        Assert.Equal(["org-shared", "own-server", "via-share"], names);
        Assert.DoesNotContain(orgHidden.Name, names);
        Assert.DoesNotContain(notShared.Name, names);
    }

    [Fact]
    public async Task WithLicense_EmptyShares_HidesOrg()
    {
        var (access, mcp, _, _) = Build(licensed: true);
        mcp.Add("alice", "own");
        mcp.Add(McpServerRecord.OrgOwner, "org");

        var names = (await access.ListMcpServersAsync("alice")).Select(s => s.Name).ToList();
        Assert.Equal(["own"], names);
    }

    [Fact]
    public async Task ResolveStrict_ThrowsForInaccessibleIds()
    {
        var (access, mcp, _, _) = Build(licensed: false);
        var own = mcp.Add("alice", "own");
        var foreign = mcp.Add("bob", "foreign");
        var org = mcp.Add(McpServerRecord.OrgOwner, "org");

        var resolved = await access.ResolveMcpServersAsync("alice", [own.Id, org.Id], strict: true);
        Assert.Equal(2, resolved.Count);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            access.ResolveMcpServersAsync("alice", [own.Id, foreign.Id, "missing"], strict: true));
        Assert.Contains(foreign.Id, error.Message);
        Assert.Contains("missing", error.Message);
    }

    [Fact]
    public async Task ResolveLenient_DropsInaccessibleIds()
    {
        var (access, mcp, _, shares) = Build(licensed: true);
        var own = mcp.Add("alice", "own");
        var foreign = mcp.Add("bob", "foreign");
        var org = mcp.Add(McpServerRecord.OrgOwner, "org");
        shares.Ids = [];

        var resolved = await access.ResolveMcpServersAsync(
            "alice", [own.Id, foreign.Id, org.Id], strict: false);
        Assert.Equal([own.Id], resolved.Select(r => r.Id));
    }

    [Fact]
    public async Task ResolveLenient_WithLicense_KeepsSharedOrgAndPersonal()
    {
        var (access, mcp, _, shares) = Build(licensed: true);
        var own = mcp.Add("alice", "own");
        var foreign = mcp.Add("bob", "foreign");
        var org = mcp.Add(McpServerRecord.OrgOwner, "org");
        shares.Ids = [foreign.Id, org.Id];

        var resolved = await access.ResolveMcpServersAsync(
            "alice", [own.Id, foreign.Id, org.Id], strict: false);
        Assert.Equal(
            new[] { own.Id, foreign.Id, org.Id }.OrderBy(id => id),
            resolved.Select(r => r.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task WithLicense_SharedItemsAppear_ViaUserGroupAndAll()
    {
        var (access, mcp, _, shares) = BuildWithShareStore(licensed: true);
        shares.KnownUsers.Add("alice");
        shares.AddMembership("alice", "devs");
        var viaAll = mcp.Add("bob", "via-all");
        var viaUser = mcp.Add("bob", "via-user");
        var viaGroup = mcp.Add("bob", "via-group");
        var notShared = mcp.Add("bob", "not-shared");

        await shares.SetSharesAsync(LibraryItemTypes.Mcp, viaAll.Id, all: true, null, null, "bob");
        await shares.SetSharesAsync(LibraryItemTypes.Mcp, viaUser.Id, all: false, ["alice"], null, "bob");
        await shares.SetSharesAsync(LibraryItemTypes.Mcp, viaGroup.Id, all: false, null, ["devs"], "bob");

        var names = (await access.ListMcpServersAsync("alice")).Select(s => s.Name).ToList();
        Assert.Equal(["via-all", "via-group", "via-user"], names);
        Assert.DoesNotContain(notShared.Name, names);
    }

    [Fact]
    public async Task WithoutLicense_ShareMatrixIsIgnored()
    {
        var (access, mcp, _, shares) = BuildWithShareStore(licensed: false);
        shares.KnownUsers.Add("alice");
        var foreign = mcp.Add("bob", "shared-server");
        var org = mcp.Add(McpServerRecord.OrgOwner, "org-server");
        await shares.SetSharesAsync(LibraryItemTypes.Mcp, foreign.Id, all: true, null, null, "bob");

        var names = (await access.ListMcpServersAsync("alice")).Select(s => s.Name).ToList();
        Assert.Equal(["org-server"], names);
        Assert.DoesNotContain(foreign.Name, names);
    }

    [Fact]
    public async Task SkillPayloads_OwnSkillWinsNameConflict()
    {
        var (access, _, skills, shares) = BuildWithShareStore(licensed: true);
        skills.Add("alice", "shared-name", content: "# mine");
        var foreign = skills.Add("bob", "shared-name", content: "# theirs");
        await shares.SetSharesAsync(LibraryItemTypes.Skill, foreign.Id, all: true, null, null, "bob");

        var payloads = await access.ListSkillPayloadsAsync("alice", projectId: null);
        Assert.Equal(["shared-name"], payloads.Select(p => p.Name));
        Assert.Equal("# mine", payloads[0].Content);
    }
}
