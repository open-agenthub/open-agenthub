using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The token surface for sharing: the same outcomes as the web dialog, spelled as status codes a
/// script can branch on. The service is faked, so these are about the controller's translation.
/// </summary>
public class RemoteSharingControllerTests
{
    private const string ValidToken = "oah_testtoken1234567890abcdef";
    private static readonly Func<string, CancellationToken, Task<string?>> Alice = (_, _) => Task.FromResult<string?>("alice");
    private static readonly Func<string, CancellationToken, Task<string?>> Nobody = (_, _) => Task.FromResult<string?>(null);

    [Fact]
    public async Task WithoutAValidToken_EveryActionIsUnauthorizedAndNeverReachesTheService()
    {
        var service = new FakeSharing();
        var controller = Remote(Nobody, service, "oah_unknown");

        Assert.IsType<UnauthorizedResult>(await controller.ListSharedWithMe(CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.List("s1", CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.ShareWithUser("s1", new CreateUserShareRequest("bob", ShareRole.Viewer), CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.UnshareUser("s1", "bob", CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.CreateLink("s1", new CreateShareLinkRequest(ShareRole.Viewer, null), CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.DeleteLink("s1", "l1", CancellationToken.None));
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task ANonPersonalBearer_IsNotLookedUpAsAToken()
    {
        // A share-link token or an OAuth access token in the header must not be hashed against
        // the api_tokens table; the oah_ prefix is what keeps the two token spaces apart.
        var lookups = 0;
        Func<string, CancellationToken, Task<string?>> counting = (_, _) => { lookups++; return Task.FromResult<string?>("alice"); };
        var controller = Remote(counting, new FakeSharing(), "eyJhbGciOiJSUzI1NiJ9.not-a-personal-token");

        Assert.IsType<UnauthorizedResult>(await controller.List("s1", CancellationToken.None));
        Assert.Equal(0, lookups);
    }

    [Fact]
    public async Task ShareWithUser_PassesTheTokenOwnerAndRoleThrough()
    {
        var service = new FakeSharing();
        var controller = Remote(Alice, service, ValidToken);

        var result = await controller.ShareWithUser("s1", new CreateUserShareRequest("bob", ShareRole.Collaborator), CancellationToken.None);

        var share = Assert.IsType<DirectSessionShare>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("bob", share.Recipient);
        Assert.Equal(ShareRole.Collaborator, share.Role);
        Assert.Equal(["share alice s1 bob Collaborator"], service.Calls);
    }

    [Fact]
    public async Task WithoutALicense_Answers402LikeTheWebSurface()
    {
        var service = new FakeSharing { Throw = new LicenseRequiredException() };
        var controller = Remote(Alice, service, ValidToken);

        foreach (var result in new[]
                 {
                     await controller.ListSharedWithMe(CancellationToken.None),
                     await controller.List("s1", CancellationToken.None),
                     await controller.ShareWithUser("s1", new CreateUserShareRequest("bob", ShareRole.Viewer), CancellationToken.None),
                     await controller.UnshareUser("s1", "bob", CancellationToken.None),
                     await controller.CreateLink("s1", new CreateShareLinkRequest(ShareRole.Viewer, null), CancellationToken.None),
                     await controller.DeleteLink("s1", "l1", CancellationToken.None)
                 })
        {
            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status402PaymentRequired, status.StatusCode);
        }
    }

    [Fact]
    public async Task ASessionThatIsNotTheOwners_Is404_WhateverTheOperation()
    {
        var service = new FakeSharing { Throw = new KeyNotFoundException() };
        var controller = Remote(Alice, service, ValidToken);

        Assert.IsType<NotFoundResult>(await controller.List("s1", CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.ShareWithUser("s1", new CreateUserShareRequest("bob", ShareRole.Viewer), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.UnshareUser("s1", "bob", CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.CreateLink("s1", new CreateShareLinkRequest(ShareRole.Viewer, null), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.DeleteLink("s1", "l1", CancellationToken.None));
    }

    [Fact]
    public async Task AnUnknownRecipient_Is400WithACodeTheClientCanBranchOn()
    {
        var service = new FakeSharing { Throw = new UnknownRecipientException("ghost") };
        var controller = Remote(Alice, service, ValidToken);

        var result = await controller.ShareWithUser("s1", new CreateUserShareRequest("ghost", ShareRole.Viewer), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal("unknown_recipient", bad.Value!.GetType().GetProperty("code")!.GetValue(bad.Value));
        Assert.Contains("known user", (string)bad.Value.GetType().GetProperty("error")!.GetValue(bad.Value)!);
    }

    [Fact]
    public async Task OtherBadInput_Is400WithoutACode()
    {
        var service = new FakeSharing { Throw = new ArgumentException("Expiration must be in the future.") };
        var controller = Remote(Alice, service, ValidToken);

        var result = await controller.CreateLink("s1", new CreateShareLinkRequest(ShareRole.Viewer, DateTime.UnixEpoch), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(bad.Value!.GetType().GetProperty("code"));
    }

    [Fact]
    public async Task CreateLink_ReturnsTheServiceUrlUntouched()
    {
        // The service builds the absolute URL; the controller must not re-derive it from the
        // request, which is the whole point of not trusting the Host header.
        var service = new FakeSharing();
        var controller = Remote(Alice, service, ValidToken);

        var result = await controller.CreateLink("s1", new CreateShareLinkRequest(ShareRole.Collaborator, null), CancellationToken.None);

        var created = Assert.IsType<CreatedShareLinkResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("https://hub.example.com/shared/tok", created.Url);
        Assert.Equal("link-1", created.Link.Id);
        Assert.Equal(["link alice s1 Collaborator"], service.Calls);
    }

    [Fact]
    public async Task Deletes_Answer204AndNameTheOwner()
    {
        var service = new FakeSharing();
        var controller = Remote(Alice, service, ValidToken);

        Assert.IsType<NoContentResult>(await controller.UnshareUser("s1", "bob", CancellationToken.None));
        Assert.IsType<NoContentResult>(await controller.DeleteLink("s1", "l1", CancellationToken.None));
        Assert.Equal(["unshare alice s1 bob", "delete-link alice s1 l1"], service.Calls);
    }

    [Fact]
    public async Task ListSharedWithMe_ReturnsTheSanitisedList()
    {
        var service = new FakeSharing
        {
            SharedWith = [new SharedSessionInfo { Id = "s9", Title = "Theirs", Owner = "carol", Mode = SessionMode.Interactive, Phase = "Running", AccessRole = "Viewer", SharedBy = "carol" }]
        };
        var controller = Remote(Alice, service, ValidToken);

        var result = await controller.ListSharedWithMe(CancellationToken.None);

        var list = Assert.IsAssignableFrom<IReadOnlyList<SharedSessionInfo>>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("s9", Assert.Single(list).Id);
        Assert.Equal(["shared-with alice"], service.Calls);
    }

    // ------------------------------------------------------------------ fixtures

    private static RemoteSharingController Remote(
        Func<string, CancellationToken, Task<string?>> findOwner, FakeSharing service, string? bearerToken)
    {
        var controller = new RemoteSharingController(findOwner, service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (bearerToken is not null)
            controller.Request.Headers.Authorization = $"Bearer {bearerToken}";
        return controller;
    }

    internal sealed class FakeSharing : ISessionSharingService
    {
        public Exception? Throw { get; init; }
        public List<string> Calls { get; } = [];
        public IReadOnlyList<SharedSessionInfo> SharedWith { get; init; } = [];

        private void Record(string call)
        {
            if (Throw is not null) throw Throw;
            Calls.Add(call);
        }

        public Task<SessionSharingOverview> ListAsync(string owner, string sessionId, CancellationToken ct = default)
        {
            Record($"list {owner} {sessionId}");
            return Task.FromResult(new SessionSharingOverview([], [], null));
        }

        public Task<IReadOnlyList<SharedSessionInfo>> ListSharedWithAsync(string recipient, CancellationToken ct = default)
        {
            Record($"shared-with {recipient}");
            return Task.FromResult(SharedWith);
        }

        public Task<DirectSessionShare> ShareWithUserAsync(string owner, string sessionId, string recipient, ShareRole role, CancellationToken ct = default)
        {
            Record($"share {owner} {sessionId} {recipient} {role}");
            return Task.FromResult(new DirectSessionShare(recipient, role, DateTime.UtcNow, DateTime.UtcNow));
        }

        public Task UnshareUserAsync(string owner, string sessionId, string recipient, CancellationToken ct = default)
        {
            Record($"unshare {owner} {sessionId} {recipient}");
            return Task.CompletedTask;
        }

        public Task<CreatedShareLinkResponse> CreateLinkAsync(string owner, string sessionId, ShareRole role, DateTime? expiresAt, CancellationToken ct = default)
        {
            Record($"link {owner} {sessionId} {role}");
            return Task.FromResult(new CreatedShareLinkResponse(
                new SessionShareLink("link-1", role, expiresAt, DateTime.UtcNow, DateTime.UtcNow, null),
                "https://hub.example.com/shared/tok"));
        }

        public Task<SessionShareLink> UpdateLinkAsync(string owner, string sessionId, string linkId, ShareRole role, DateTime? expiresAt, CancellationToken ct = default)
        {
            Record($"update-link {owner} {sessionId} {linkId} {role}");
            return Task.FromResult(new SessionShareLink(linkId, role, expiresAt, DateTime.UtcNow, DateTime.UtcNow, null));
        }

        public Task DeleteLinkAsync(string owner, string sessionId, string linkId, CancellationToken ct = default)
        {
            Record($"delete-link {owner} {sessionId} {linkId}");
            return Task.CompletedTask;
        }

        public Task<SessionMcpPolicy?> SetMcpPolicyAsync(string owner, string sessionId, IReadOnlyCollection<string>? blockedServers, IReadOnlyCollection<string>? blockedTools, CancellationToken ct = default)
        {
            Record($"policy {owner} {sessionId}");
            return Task.FromResult<SessionMcpPolicy?>(null);
        }
    }
}
