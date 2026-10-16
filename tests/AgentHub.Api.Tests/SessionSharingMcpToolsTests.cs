using System.Security.Claims;
using AgentHub.Api.Ee.Sharing;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The remote MCP sharing tools: parameters arrive as strings and leave as the service's typed
/// arguments, and the service's exceptions leave as the stable codes the stdio server uses too.
/// </summary>
public class SessionSharingMcpToolsTests
{
    [Fact]
    public async Task SessionShare_DefaultsToViewerAndRunsAsTheSignedInUser()
    {
        var service = new RemoteSharingControllerTests.FakeSharing();
        var tools = Tools(service);

        var share = await tools.Share("s1", "bob");

        Assert.Equal(ShareRole.Viewer, share.Role);
        Assert.Equal(["share alice s1 bob Viewer"], service.Calls);
    }

    [Fact]
    public async Task SessionShare_ReadsTheRoleCaseInsensitivelyAndRejectsAnUnknownOne()
    {
        var service = new RemoteSharingControllerTests.FakeSharing();
        var tools = Tools(service);

        await tools.Share("s1", "bob", "collaborator");
        Assert.Equal(["share alice s1 bob Collaborator"], service.Calls);

        // A typo must not quietly become a Viewer grant — or a Collaborator one.
        var error = await Assert.ThrowsAsync<McpException>(() => tools.Share("s1", "bob", "Admin"));
        Assert.Equal("invalid_role", error.Message);
        Assert.Single(service.Calls);
    }

    [Fact]
    public async Task SessionUnshare_ReturnsTheRecipientItRemoved()
    {
        var service = new RemoteSharingControllerTests.FakeSharing();

        Assert.Equal("bob", await Tools(service).Unshare("s1", "bob"));
        Assert.Equal(["unshare alice s1 bob"], service.Calls);
    }

    [Fact]
    public async Task SessionShareLink_ReturnsTheUrlAndTheLinkIdAndParsesTheExpiryAsUtc()
    {
        var service = new RemoteSharingControllerTests.FakeSharing();

        var result = await Tools(service).CreateLink("s1", "Collaborator", "2030-01-02T03:04:05Z");

        Assert.Equal("link-1", result.LinkId);
        Assert.Equal("https://hub.example.com/shared/tok", result.Url);
        Assert.Equal("Collaborator", result.Role);
        Assert.Equal(new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc), result.ExpiresAt);
        Assert.Equal(DateTimeKind.Utc, result.ExpiresAt!.Value.Kind);
    }

    [Fact]
    public async Task SessionShareLink_WithoutAnExpiry_MintsAnOpenEndedLink_AndRejectsGarbage()
    {
        var service = new RemoteSharingControllerTests.FakeSharing();
        var tools = Tools(service);

        var open = await tools.CreateLink("s1");
        Assert.Null(open.ExpiresAt);
        Assert.Equal("Viewer", open.Role);

        var error = await Assert.ThrowsAsync<McpException>(() => tools.CreateLink("s1", null, "next tuesday"));
        Assert.Equal("invalid_expires_at", error.Message);
    }

    [Fact]
    public async Task SessionShares_ListsTheOverview()
    {
        var service = new RemoteSharingControllerTests.FakeSharing();

        var overview = await Tools(service).ListShares("s1");

        Assert.Empty(overview.Users);
        Assert.Equal(["list alice s1"], service.Calls);
    }

    [Theory]
    [InlineData(typeof(LicenseRequiredException), "license_required")]
    [InlineData(typeof(KeyNotFoundException), "session_not_found")]
    [InlineData(typeof(UnknownRecipientException), "unknown_recipient")]
    public async Task ServiceFailures_BecomeTheStableCodes(Type thrown, string expected)
    {
        var exception = thrown == typeof(UnknownRecipientException)
            ? new UnknownRecipientException("ghost")
            : (Exception)Activator.CreateInstance(thrown)!;
        var tools = Tools(new RemoteSharingControllerTests.FakeSharing { Throw = exception });

        Assert.Equal(expected, (await Assert.ThrowsAsync<McpException>(() => tools.Share("s1", "ghost"))).Message);
        Assert.Equal(expected, (await Assert.ThrowsAsync<McpException>(() => tools.CreateLink("s1"))).Message);
        Assert.Equal(expected, (await Assert.ThrowsAsync<McpException>(() => tools.ListShares("s1"))).Message);
    }

    [Fact]
    public async Task OtherBadInput_KeepsTheMessageBehindAStablePrefix()
    {
        var tools = Tools(new RemoteSharingControllerTests.FakeSharing
        {
            Throw = new ArgumentException("A session owner cannot share with themselves.")
        });

        var error = await Assert.ThrowsAsync<McpException>(() => tools.Share("s1", "alice"));

        Assert.StartsWith("invalid_request: ", error.Message);
        Assert.Contains("themselves", error.Message);
    }

    [Fact]
    public async Task WithoutAnIdentity_RefusesRatherThanActingAsSomebody()
    {
        var service = new RemoteSharingControllerTests.FakeSharing();
        var tools = new SessionSharingMcpTools(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, service);

        var error = await Assert.ThrowsAsync<McpException>(() => tools.Share("s1", "bob"));

        Assert.Equal("unauthenticated", error.Message);
        Assert.Empty(service.Calls);
    }

    private static SessionSharingMcpTools Tools(ISessionSharingService service)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "test"))
        };
        return new SessionSharingMcpTools(new HttpContextAccessor { HttpContext = context }, service);
    }
}
