using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
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
        InMemoryMcpServerStore? mcp = null)
    {
        var controller = new LibraryController(
            shares ?? new InMemoryLibraryShareStore(),
            mcp ?? new InMemoryMcpServerStore(),
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
    public async Task WithoutLicense_SharesReturn402()
    {
        var mcp = new InMemoryMcpServerStore();
        var server = mcp.Add("admin", "docs");
        var controller = Controller("admin", licensed: false, mcp: mcp);

        Assert.Equal(402, ((ObjectResult)await controller.GetShares(server.Id, default)).StatusCode);
        Assert.Equal(402, ((ObjectResult)await controller.SetShares(
            server.Id, new UpdateSharesRequest(true, null, null), default)).StatusCode);
    }

    [Fact]
    public async Task PersonalOwner_CanShareWithUsersGroupsAndAll()
    {
        var shares = new InMemoryLibraryShareStore();
        shares.KnownUsers.Add("carol");
        shares.KnownGroups.Add("devs");
        var mcp = new InMemoryMcpServerStore();
        var server = mcp.Add("alice", "docs");

        var controller = Controller("alice", licensed: true, shares, mcp);
        var result = Assert.IsType<OkObjectResult>(await controller.SetShares(
            server.Id, new UpdateSharesRequest(true, ["carol"], ["devs"]), default));
        var state = Assert.IsType<LibraryShares>(result.Value);
        Assert.True(state.All);
        Assert.Equal(["carol"], state.Users);
        Assert.Equal(["devs"], state.Groups);

        var get = Assert.IsType<OkObjectResult>(await controller.GetShares(server.Id, default));
        Assert.Equal(state, Assert.IsType<LibraryShares>(get.Value));
    }

    [Fact]
    public async Task Admin_CanShareOrgEntry()
    {
        var shares = new InMemoryLibraryShareStore();
        shares.KnownUsers.Add("carol");
        var mcp = new InMemoryMcpServerStore();
        var server = mcp.Add(McpServerRecord.OrgOwner, "org-docs");

        var controller = Controller("admin", licensed: true, shares, mcp);
        var result = Assert.IsType<OkObjectResult>(await controller.SetShares(
            server.Id, new UpdateSharesRequest(false, ["carol"], null), default));
        Assert.Equal(["carol"], Assert.IsType<LibraryShares>(result.Value).Users);
    }

    [Fact]
    public async Task NonAdmin_CannotShareOrgEntry()
    {
        var mcp = new InMemoryMcpServerStore();
        var server = mcp.Add(McpServerRecord.OrgOwner, "org-docs");
        var controller = Controller("carol", licensed: true, mcp: mcp);

        Assert.IsType<NotFoundResult>(await controller.GetShares(server.Id, default));
        Assert.IsType<NotFoundResult>(await controller.SetShares(
            server.Id, new UpdateSharesRequest(true, null, null), default));
    }

    [Fact]
    public async Task NonOwner_CannotTouchPersonalShares()
    {
        var mcp = new InMemoryMcpServerStore();
        var server = mcp.Add("someone-else", "docs");
        var controller = Controller("admin", licensed: true, mcp: mcp);

        Assert.IsType<NotFoundResult>(await controller.GetShares(server.Id, default));
        Assert.IsType<NotFoundResult>(await controller.SetShares(
            server.Id, new UpdateSharesRequest(true, null, null), default));
    }

    [Fact]
    public async Task UnknownItem_IsNotFound()
    {
        var controller = Controller("admin", licensed: true);
        Assert.IsType<NotFoundResult>(await controller.GetShares("missing", default));
    }
}
