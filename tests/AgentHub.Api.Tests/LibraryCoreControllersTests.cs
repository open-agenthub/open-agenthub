using System.Security.Claims;
using System.Text.Json;
using AgentHub.Api.Admin;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibraryCoreControllersTests
{
    private static AdminAccess Admins(string admins = "admin") => new(
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ee:Admins"] = admins })
            .Build(),
        NullLogger<AdminAccess>.Instance,
        []);

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

    private static (InMemoryMcpServerStore Mcp, InMemoryLibraryShareStore Shares, LibraryAccessService Access)
        Stores(bool licensed = true)
    {
        var mcp = new InMemoryMcpServerStore();
        var shares = new InMemoryLibraryShareStore();
        return (mcp, shares, new LibraryAccessService(mcp, shares, new FakeEnterpriseLicense(licensed)));
    }

    private static SaveMcpServerRequest Raw(
        string name,
        string? description = null,
        string configJson = "{\"type\":\"http\",\"url\":\"https://docs.example.test\"}",
        string? secretJson = null) =>
        new(name, description, "raw", configJson, secretJson);

    [Fact]
    public async Task McpList_HidesConfigOfSharedAndOrgEntries()
    {
        var (mcp, shares, access) = Stores();
        mcp.Add("alice", "own");
        var foreign = mcp.Add("bob", "shared", configJson: "{\"type\":\"http\",\"url\":\"https://secret.example.test?token=x\"}");
        mcp.Add(McpServerRecord.OrgOwner, "org-docs", configJson: "{\"type\":\"http\",\"url\":\"https://org.example.test\"}");
        await shares.SetSharesAsync(LibraryItemTypes.Mcp, foreign.Id, all: true, null, null, "bob");

        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");
        var list = await controller.List(default);

        var own = list.Single(i => i.Name == "own");
        Assert.True(own.Mine);
        Assert.NotNull(own.ConfigJson);
        Assert.Equal("raw", own.Kind);

        var shared = list.Single(i => i.Name == "shared");
        Assert.False(shared.Mine);
        Assert.Equal("bob", shared.Owner);
        Assert.Null(shared.ConfigJson);

        // Org is not visible via shares-only EE access unless shared; without share it's absent.
        Assert.DoesNotContain(list, i => i.Name == "org-docs");
    }

    [Fact]
    public async Task McpList_WithoutLicense_IncludesOrgButHidesConfig()
    {
        var (mcp, shares, access) = Stores(licensed: false);
        mcp.Add("alice", "own");
        mcp.Add(McpServerRecord.OrgOwner, "org-docs",
            configJson: "{\"type\":\"http\",\"url\":\"https://org.example.test\"}");

        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");
        var list = await controller.List(default);

        var org = list.Single(i => i.Name == "org-docs");
        Assert.False(org.Mine);
        Assert.Equal(McpServerRecord.OrgOwner, org.Owner);
        Assert.Null(org.ConfigJson);
    }

    [Fact]
    public async Task McpCreate_ValidationErrorsBecome400()
    {
        var (mcp, shares, access) = Stores();
        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");

        var bad = Assert.IsType<BadRequestObjectResult>(
            await controller.Create(Raw("bad name"), default));
        Assert.NotNull(bad.Value);

        Assert.IsType<OkObjectResult>(await controller.Create(Raw("docs"), default));
        Assert.IsType<BadRequestObjectResult>(await controller.Create(Raw("docs"), default));
    }

    [Fact]
    public async Task McpCreate_NeverReturnsSecretJson()
    {
        var (mcp, shares, access) = Stores();
        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");

        var ok = Assert.IsType<OkObjectResult>(
            await controller.Create(Raw("docs", secretJson: "{\"token\":\"s3cr3t\"}"), default));
        var info = Assert.IsType<McpServerInfo>(ok.Value);
        Assert.True(info.Mine);
        Assert.NotNull(info.ConfigJson);
        Assert.DoesNotContain("s3cr3t", JsonSerializer.Serialize(info));
        Assert.Equal("{\"token\":\"s3cr3t\"}", (await mcp.ListByOwnerAsync("alice")).Single().SecretJson);
    }

    [Fact]
    public async Task McpUpdate_NotFoundAndValidation()
    {
        var (mcp, shares, access) = Stores();
        var created = mcp.Add("alice", "docs");
        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");

        Assert.IsType<NotFoundResult>(
            await controller.Update("missing", Raw("docs"), default));
        Assert.IsType<BadRequestObjectResult>(
            await controller.Update(created.Id, Raw("bad name"), default));

        var ok = Assert.IsType<OkObjectResult>(
            await controller.Update(created.Id, Raw("docs2", "updated"), default));
        Assert.Equal("docs2", Assert.IsType<McpServerInfo>(ok.Value).Name);
    }

    [Fact]
    public async Task McpUpdate_NullSecretJson_LeavesExistingSecret()
    {
        var (mcp, shares, access) = Stores();
        var created = await mcp.CreateAsync(
            "alice", Raw("docs", secretJson: "{\"token\":\"keep-me\"}"));
        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");

        var ok = Assert.IsType<OkObjectResult>(
            await controller.Update(created.Id, Raw("docs", "x", secretJson: null), default));
        Assert.IsType<McpServerInfo>(ok.Value);
        Assert.Equal("{\"token\":\"keep-me\"}", (await mcp.GetManyAsync([created.Id])).Single().SecretJson);
    }

    [Fact]
    public async Task McpUpdate_EmptySecretJson_ClearsSecret()
    {
        var (mcp, shares, access) = Stores();
        var created = await mcp.CreateAsync(
            "alice", Raw("docs", secretJson: "{\"token\":\"drop-me\"}"));
        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");

        Assert.IsType<OkObjectResult>(
            await controller.Update(created.Id, Raw("docs", secretJson: ""), default));
        Assert.Null((await mcp.GetManyAsync([created.Id])).Single().SecretJson);
    }

    [Fact]
    public async Task McpDelete_RemovesSharesToo()
    {
        var (mcp, shares, access) = Stores();
        var server = mcp.Add("alice", "docs");
        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");

        Assert.IsType<NoContentResult>(await controller.Delete(server.Id, default));
        Assert.Contains((LibraryItemTypes.Mcp, server.Id), shares.DeletedItems);

        Assert.IsType<NotFoundResult>(await controller.Delete(server.Id, default));
    }

    [Fact]
    public async Task McpDelete_OtherOwnersEntry_IsNotFound()
    {
        var (mcp, shares, access) = Stores();
        var foreign = mcp.Add("bob", "docs");
        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");

        Assert.IsType<NotFoundResult>(await controller.Delete(foreign.Id, default));
        Assert.Empty(shares.DeletedItems);
    }

    [Fact]
    public async Task FromApi_CreatesApiKindWithConfig()
    {
        var (mcp, shares, access) = Stores();
        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");

        var ok = Assert.IsType<OkObjectResult>(await controller.CreateFromApi(
            new CreateMcpFromApiRequest(
                Name: "petstore",
                Description: "Pets",
                SpecUrl: "https://api.example.test/openapi.json",
                SpecType: "openapi",
                BaseUrl: "https://api.example.test",
                Auth: JsonSerializer.SerializeToElement(new { type = "bearer" }),
                Secret: "tok-123",
                Save: true),
            default));
        var info = Assert.IsType<McpServerInfo>(ok.Value);
        Assert.Equal("api", info.Kind);
        Assert.True(info.Mine);
        Assert.Contains("specUrl", info.ConfigJson!);
        Assert.Contains("openapi", info.ConfigJson!);
        Assert.Contains("baseUrl", info.ConfigJson!);

        var stored = (await mcp.ListByOwnerAsync("alice")).Single();
        Assert.Equal("api", stored.Kind);
        Assert.Contains("\"token\":\"tok-123\"", stored.SecretJson);
    }

    [Fact]
    public async Task FromApi_MissingSpecUrl_Is400()
    {
        var (mcp, shares, access) = Stores();
        var controller = WithUser(new McpServersController(mcp, access, shares), "alice");

        Assert.IsType<BadRequestObjectResult>(await controller.CreateFromApi(
            new CreateMcpFromApiRequest("x", null, "", null, null, null, null, true),
            default));
    }

    [Fact]
    public async Task AdminList_ReturnsOrgConfig_ForAdmin()
    {
        var (mcp, shares, _) = Stores();
        mcp.Add(McpServerRecord.OrgOwner, "org-docs",
            configJson: "{\"type\":\"http\",\"url\":\"https://org.example.test\"}");
        mcp.Add("alice", "personal");

        var controller = WithUser(
            new AdminMcpServersController(mcp, shares, Admins()), "admin");
        var ok = Assert.IsType<OkObjectResult>(await controller.List(default));
        var list = Assert.IsAssignableFrom<IReadOnlyList<McpServerInfo>>(ok.Value);

        var org = Assert.Single(list);
        Assert.Equal("org-docs", org.Name);
        Assert.True(org.Mine);
        Assert.NotNull(org.ConfigJson);
        Assert.Equal(McpServerRecord.OrgOwner, org.Owner);
    }

    [Fact]
    public async Task AdminCreateUpdateDelete_ForcesOrgOwner()
    {
        var (mcp, shares, _) = Stores();
        var controller = WithUser(
            new AdminMcpServersController(mcp, shares, Admins()), "admin");

        var created = Assert.IsType<OkObjectResult>(
            await controller.Create(Raw("org-api"), default));
        var info = Assert.IsType<McpServerInfo>(created.Value);
        Assert.Equal(McpServerRecord.OrgOwner, info.Owner);
        Assert.True(info.Mine);

        var updated = Assert.IsType<OkObjectResult>(
            await controller.Update(info.Id, Raw("org-api2"), default));
        Assert.Equal("org-api2", Assert.IsType<McpServerInfo>(updated.Value).Name);

        Assert.IsType<NoContentResult>(await controller.Delete(info.Id, default));
        Assert.Contains((LibraryItemTypes.Mcp, info.Id), shares.DeletedItems);
        Assert.Empty(await mcp.ListByOwnerAsync(McpServerRecord.OrgOwner));
    }

    [Fact]
    public async Task AdminEndpoints_ForbidNonAdmin()
    {
        var (mcp, shares, _) = Stores();
        var controller = WithUser(
            new AdminMcpServersController(mcp, shares, Admins("admin")), "carol");

        Assert.IsType<ForbidResult>(await controller.List(default));
        Assert.IsType<ForbidResult>(await controller.Create(Raw("x"), default));
        Assert.IsType<ForbidResult>(await controller.Update("id", Raw("x"), default));
        Assert.IsType<ForbidResult>(await controller.Delete("id", default));
    }

    [Fact]
    public async Task AdminUpdate_CannotTouchPersonalEntries()
    {
        var (mcp, shares, _) = Stores();
        var personal = mcp.Add("alice", "docs");
        var controller = WithUser(
            new AdminMcpServersController(mcp, shares, Admins()), "admin");

        Assert.IsType<NotFoundResult>(
            await controller.Update(personal.Id, Raw("stolen"), default));
        Assert.IsType<NotFoundResult>(await controller.Delete(personal.Id, default));
    }
}
