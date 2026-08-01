using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Ee.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibraryControllerTests
{
    private static AdminAccess Admins(string admins = "admin") => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ee:Admins"] = admins })
            .Build(),
        NullLogger<AdminAccess>.Instance,
        []);

    private static LibraryController Controller(
        string user,
        bool licensed,
        InMemoryLibraryShareStore? shares = null,
        InMemoryMcpServerStore? mcp = null,
        InMemorySkillStore? skills = null)
    {
        var controller = new LibraryController(
            shares ?? new InMemoryLibraryShareStore(),
            mcp ?? new InMemoryMcpServerStore(),
            skills ?? new InMemorySkillStore(),
            null!, // UserDirectory — only reached by ListUsers after passing the gate
            Admins(),
            new FakeEnterpriseLicense(licensed));
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

    [Fact]
    public async Task WithoutLicense_EverythingReturns402()
    {
        var controller = Controller("admin", licensed: false);
        Assert.Equal(402, ((ObjectResult)await controller.ListGroups(default)).StatusCode);
        Assert.Equal(402, ((ObjectResult)await controller.ListUsers(default)).StatusCode);
        Assert.Equal(402, ((ObjectResult)await controller.GetSettings(default)).StatusCode);
        Assert.Equal(402, ((ObjectResult)await controller.SetShares(
            "skills", "skill-1", new UpdateSharesRequest(true, null, null), default)).StatusCode);
    }

    [Fact]
    public async Task GroupManagement_RequiresAdmin()
    {
        var controller = Controller("carol", licensed: true);
        Assert.Equal(403, ((ObjectResult)await controller.ListGroups(default)).StatusCode);
        Assert.Equal(403, ((ObjectResult)await controller.ListUsers(default)).StatusCode);
        Assert.Equal(403, ((ObjectResult)await controller.CreateGroup(new CreateGroupRequest("devs"), default)).StatusCode);
        Assert.Equal(403, ((ObjectResult)await controller.SetSettings(
            new UpdateLibrarySettingsRequest(true), default)).StatusCode);
    }

    [Fact]
    public async Task Settings_AreReadableByRegularUsers()
    {
        var shares = new InMemoryLibraryShareStore { UserSkillPublishing = true };
        var controller = Controller("carol", licensed: true, shares);
        var result = Assert.IsType<OkObjectResult>(await controller.GetSettings(default));
        Assert.True(Assert.IsType<LibrarySettings>(result.Value).UserSkillPublishing);
    }

    [Fact]
    public async Task AdminOwner_CanShareMcpServerWithUsersGroupsAndAll()
    {
        var shares = new InMemoryLibraryShareStore();
        shares.KnownUsers.Add("carol");
        var group = await shares.CreateGroupAsync("devs");
        var mcp = new InMemoryMcpServerStore();
        var server = mcp.Add("admin", "docs");

        var controller = Controller("admin", licensed: true, shares, mcp);
        var result = Assert.IsType<OkObjectResult>(await controller.SetShares(
            "mcp-servers", server.Id, new UpdateSharesRequest(true, ["carol"], [group.Id]), default));
        var state = Assert.IsType<LibraryShares>(result.Value);
        Assert.True(state.All);
        Assert.Equal(["carol"], state.Users);
        Assert.Equal([group.Id], state.Groups);
    }

    [Fact]
    public async Task NonOwner_CannotTouchShares()
    {
        var mcp = new InMemoryMcpServerStore();
        var server = mcp.Add("someone-else", "docs");
        var controller = Controller("admin", licensed: true, mcp: mcp);

        Assert.IsType<NotFoundResult>(await controller.GetShares("mcp-servers", server.Id, default));
        Assert.IsType<NotFoundResult>(await controller.SetShares(
            "mcp-servers", server.Id, new UpdateSharesRequest(true, null, null), default));
    }

    [Fact]
    public async Task RegularUser_CanPublishOwnSkill_WhenToggleIsOn()
    {
        var shares = new InMemoryLibraryShareStore { UserSkillPublishing = true };
        var skills = new InMemorySkillStore();
        var skill = skills.Add("carol", "review");
        var controller = Controller("carol", licensed: true, shares, skills: skills);

        var result = Assert.IsType<OkObjectResult>(await controller.SetShares(
            "skills", skill.Id, new UpdateSharesRequest(true, [], []), default));
        Assert.True(Assert.IsType<LibraryShares>(result.Value).All);
    }

    [Fact]
    public async Task RegularUser_CannotPublish_WhenToggleIsOff()
    {
        var shares = new InMemoryLibraryShareStore { UserSkillPublishing = false };
        var skills = new InMemorySkillStore();
        var skill = skills.Add("carol", "review");
        var controller = Controller("carol", licensed: true, shares, skills: skills);

        Assert.IsType<ForbidResult>(await controller.SetShares(
            "skills", skill.Id, new UpdateSharesRequest(true, [], []), default));
    }

    [Fact]
    public async Task RegularUser_CannotShareWithSpecificUsersOrGroups()
    {
        var shares = new InMemoryLibraryShareStore { UserSkillPublishing = true };
        shares.KnownUsers.Add("dave");
        var skills = new InMemorySkillStore();
        var skill = skills.Add("carol", "review");
        var mcp = new InMemoryMcpServerStore();
        var server = mcp.Add("carol", "docs");
        var controller = Controller("carol", licensed: true, shares, mcp, skills);

        // Targeted sharing stays admin-only, even with the publish toggle on.
        Assert.IsType<ForbidResult>(await controller.SetShares(
            "skills", skill.Id, new UpdateSharesRequest(false, ["dave"], []), default));
        // MCP servers cannot be published by regular users at all.
        Assert.IsType<ForbidResult>(await controller.SetShares(
            "mcp-servers", server.Id, new UpdateSharesRequest(true, [], []), default));
    }

    [Fact]
    public async Task UnknownItemType_IsNotFound()
    {
        var controller = Controller("admin", licensed: true);
        Assert.IsType<NotFoundResult>(await controller.GetShares("bogus", "id", default));
    }
}
