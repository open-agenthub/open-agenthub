using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibraryCoreControllersTests
{
    private static T WithUser<T>(T controller, string user) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("preferred_username", user)], "test"))
            }
        };
        return controller;
    }

    private static (InMemoryMcpServerStore Mcp, InMemorySkillStore Skills, InMemoryLibraryShareStore Shares, LibraryAccessService Access)
        Stores(bool licensed = true)
    {
        var mcp = new InMemoryMcpServerStore();
        var skills = new InMemorySkillStore();
        var shares = new InMemoryLibraryShareStore();
        return (mcp, skills, shares, new LibraryAccessService(mcp, skills, shares, new FakeEnterpriseLicense(licensed)));
    }

    [Fact]
    public async Task McpList_HidesConfigOfSharedEntries()
    {
        var (mcp, _, shares, access) = Stores();
        mcp.Add("alice", "own");
        var foreign = mcp.Add("bob", "shared", "{\"type\":\"http\",\"url\":\"https://secret.example.test?token=x\"}");
        await shares.SetSharesAsync(LibraryItemTypes.Mcp, foreign.Id, all: true, null, null, "bob");

        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");
        var list = await controller.List(default);

        var own = list.Single(i => i.Name == "own");
        Assert.True(own.Mine);
        Assert.NotNull(own.ConfigJson);

        var shared = list.Single(i => i.Name == "shared");
        Assert.False(shared.Mine);
        Assert.Equal("bob", shared.Owner);
        Assert.Null(shared.ConfigJson); // shared configs may contain tokens
    }

    [Fact]
    public async Task McpDelete_RemovesSharesToo()
    {
        var (mcp, _, shares, access) = Stores();
        var server = mcp.Add("alice", "docs");
        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");

        Assert.IsType<NoContentResult>(await controller.Delete(server.Id, default));
        Assert.Contains((LibraryItemTypes.Mcp, server.Id), shares.DeletedItems);

        Assert.IsType<NotFoundResult>(await controller.Delete(server.Id, default));
    }

    [Fact]
    public async Task SkillGet_ReturnsContent_ForAccessibleSkills()
    {
        var (mcp, skills, shares, access) = Stores();
        _ = mcp;
        var foreign = skills.Add("bob", "review", "# shared skill body");
        await shares.SetSharesAsync(LibraryItemTypes.Skill, foreign.Id, all: true, null, null, "bob");

        var controller = WithUser(new SkillsController(skills, access, shares), "alice");
        var ok = Assert.IsType<OkObjectResult>(await controller.Get(foreign.Id, default));
        var detail = Assert.IsType<SkillDetail>(ok.Value);
        Assert.Equal("# shared skill body", detail.Content);
        Assert.False(detail.Mine);
    }

    [Fact]
    public async Task SkillGet_InaccessibleSkill_IsNotFound()
    {
        var (_, skills, shares, access) = Stores(licensed: false);
        var foreign = skills.Add("bob", "review");
        await shares.SetSharesAsync(LibraryItemTypes.Skill, foreign.Id, all: true, null, null, "bob");

        var controller = WithUser(new SkillsController(skills, access, shares), "alice");
        Assert.IsType<NotFoundResult>(await controller.Get(foreign.Id, default));
    }

    [Fact]
    public async Task SkillCreate_ValidationErrorsBecome400()
    {
        var (_, skills, shares, access) = Stores();
        var controller = WithUser(new SkillsController(skills, access, shares), "alice");

        var bad = Assert.IsType<BadRequestObjectResult>(
            await controller.Create(new SaveSkillRequest("Not Kebab", null, "# body"), default));
        Assert.NotNull(bad.Value);

        Assert.IsType<OkObjectResult>(
            await controller.Create(new SaveSkillRequest("review", null, "# body"), default));
        Assert.IsType<BadRequestObjectResult>(
            await controller.Create(new SaveSkillRequest("review", null, "# duplicate"), default));
    }
}
