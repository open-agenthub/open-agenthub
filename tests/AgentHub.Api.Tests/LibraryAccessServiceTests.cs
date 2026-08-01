using AgentHub.Api.Library;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibraryAccessServiceTests
{
    private static (LibraryAccessService Access, InMemoryMcpServerStore Mcp, FakeLibraryShareReader Shares)
        Build(bool licensed, FakeLibraryShareReader? shares = null)
    {
        var mcp = new InMemoryMcpServerStore();
        shares ??= new FakeLibraryShareReader();
        var access = new LibraryAccessService(mcp, shares, new FakeEnterpriseLicense(licensed));
        return (access, mcp, shares);
    }

    [Fact]
    public async Task WithoutLicense_OwnAndOrgAreVisible_ForeignPersonalHidden()
    {
        var (access, mcp, _) = Build(licensed: false);
        mcp.Add("alice", "own-server");
        mcp.Add(McpServerRecord.OrgOwner, "org-server");
        var foreign = mcp.Add("bob", "bob-server");
        // Even if a share were configured, without a license personal sharing is ignored.
        var shares = new FakeLibraryShareReader { Ids = [foreign.Id] };
        var accessWithShares = new LibraryAccessService(mcp, shares, new FakeEnterpriseLicense(false));

        var names = (await accessWithShares.ListMcpServersAsync("alice")).Select(s => s.Name).ToList();
        Assert.Equal(["org-server", "own-server"], names);
        Assert.DoesNotContain("bob-server", names);
    }

    [Fact]
    public async Task WithLicense_OrgRequiresShare_SharedPersonalVisible()
    {
        var (access, mcp, shares) = Build(licensed: true);
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
        var (access, mcp, _) = Build(licensed: true);
        mcp.Add("alice", "own");
        mcp.Add(McpServerRecord.OrgOwner, "org");

        var names = (await access.ListMcpServersAsync("alice")).Select(s => s.Name).ToList();
        Assert.Equal(["own"], names);
    }

    [Fact]
    public async Task ResolveStrict_ThrowsForInaccessibleIds()
    {
        var (access, mcp, _) = Build(licensed: false);
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
        var (access, mcp, shares) = Build(licensed: true);
        var own = mcp.Add("alice", "own");
        var foreign = mcp.Add("bob", "foreign");
        var org = mcp.Add(McpServerRecord.OrgOwner, "org");
        // Share was once present for foreign; license still on but share revoked → drop.
        shares.Ids = [];

        var resolved = await access.ResolveMcpServersAsync(
            "alice", [own.Id, foreign.Id, org.Id], strict: false);
        Assert.Equal([own.Id], resolved.Select(r => r.Id));
    }

    [Fact]
    public async Task ResolveLenient_WithLicense_KeepsSharedOrgAndPersonal()
    {
        var (access, mcp, shares) = Build(licensed: true);
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
}
