using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The one licence check every sharing surface relies on, and the link URL every surface hands
/// out. Both live in the service so the web controller, the token API and the MCP tools cannot
/// disagree on them.
/// </summary>
public class SessionSharingServiceTests
{
    [Fact]
    public async Task WithoutALicense_EveryOperationFailsBeforeTheStoreIsTouched()
    {
        var store = new RecordingShareStore();
        var service = new SessionSharingService(store, new FakeEnterpriseLicense(false), "https://hub.example.com");

        await Assert.ThrowsAsync<LicenseRequiredException>(() => service.ListAsync("alice", "s1"));
        await Assert.ThrowsAsync<LicenseRequiredException>(() => service.ListSharedWithAsync("alice"));
        await Assert.ThrowsAsync<LicenseRequiredException>(() => service.ShareWithUserAsync("alice", "s1", "bob", ShareRole.Viewer));
        await Assert.ThrowsAsync<LicenseRequiredException>(() => service.UnshareUserAsync("alice", "s1", "bob"));
        await Assert.ThrowsAsync<LicenseRequiredException>(() => service.CreateLinkAsync("alice", "s1", ShareRole.Viewer, null));
        await Assert.ThrowsAsync<LicenseRequiredException>(() => service.UpdateLinkAsync("alice", "s1", "l1", ShareRole.Viewer, null));
        await Assert.ThrowsAsync<LicenseRequiredException>(() => service.DeleteLinkAsync("alice", "s1", "l1"));
        await Assert.ThrowsAsync<LicenseRequiredException>(() => service.SetMcpPolicyAsync("alice", "s1", [], []));

        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task WithALicense_DelegatesToTheStoreWithTheCallersOwner()
    {
        var store = new RecordingShareStore();
        var service = new SessionSharingService(store, new FakeEnterpriseLicense(true), null);

        var share = await service.ShareWithUserAsync("alice", "s1", "bob", ShareRole.Collaborator);
        await service.UnshareUserAsync("alice", "s1", "bob");
        await service.DeleteLinkAsync("alice", "s1", "l1");

        Assert.Equal("bob", share.Recipient);
        Assert.Equal(ShareRole.Collaborator, share.Role);
        Assert.Equal(["upsert alice s1 bob Collaborator", "delete-direct alice s1 bob", "delete-link alice s1 l1"], store.Calls);
    }

    [Fact]
    public async Task CreateLink_BuildsAnAbsoluteUrlFromTheFrontendOrigin()
    {
        // The token is shown exactly once; a caller outside a browser needs something it can open
        // without resolving a path against an origin it does not have.
        var store = new RecordingShareStore();
        var service = new SessionSharingService(store, new FakeEnterpriseLicense(true), "https://hub.example.com/");

        var created = await service.CreateLinkAsync("alice", "s1", ShareRole.Viewer, null);

        Assert.Equal("https://hub.example.com/shared/" + RecordingShareStore.Token, created.Url);
        Assert.Equal("link-1", created.Link.Id);
    }

    [Fact]
    public async Task CreateLink_WithoutAFrontendOrigin_FallsBackToThePathRatherThanDroppingTheToken()
    {
        var service = new SessionSharingService(new RecordingShareStore(), new FakeEnterpriseLicense(true), "  ");

        var created = await service.CreateLinkAsync("alice", "s1", ShareRole.Viewer, null);

        Assert.Equal("/shared/" + RecordingShareStore.Token, created.Url);
    }

    [Fact]
    public void ShareLinkUrl_EscapesTheToken()
        => Assert.Equal("https://h.example/shared/a%2Fb", ShareLinkUrl.For("https://h.example", "a/b"));

    [Fact]
    public async Task ListSharedWith_ReturnsTheSanitisedShapeWithRoleAndSharer()
    {
        var store = new RecordingShareStore
        {
            SharedWith =
            [
                new StoredSessionAccess(Session("s1", "owner-1"), ShareRole.Collaborator),
                new StoredSessionAccess(Session("s2", "owner-2"), ShareRole.Viewer)
            ]
        };
        var service = new SessionSharingService(store, new FakeEnterpriseLicense(true), null);

        var shared = await service.ListSharedWithAsync("bob");

        Assert.Equal(["s1", "s2"], shared.Select(s => s.Id));
        Assert.Equal("Collaborator", shared[0].AccessRole);
        Assert.True(shared[0].CanWrite);
        Assert.Equal("owner-1", shared[0].SharedBy);
        Assert.Equal("Viewer", shared[1].AccessRole);
        Assert.False(shared[1].CanWrite);
        Assert.All(shared, s => Assert.False(s.CanManage));
        // Owner-only data must not ride along just because the row came from the sessions table.
        Assert.All(shared, s => Assert.Null(s.McpConfigJson));
    }

    private static SessionRecord Session(string id, string owner) => new()
    {
        Id = id, Owner = owner, Title = id, Mode = SessionMode.Interactive,
        McpConfigJson = """{"mcpServers":{"private":{}}}""",
        ClaudeSessionId = "c-" + id, CallbackToken = "cb-" + id, Status = "Running"
    };

    internal sealed class RecordingShareStore : ISessionShareStore
    {
        public const string Token = "tok_abcdefghijklmnopqrstuvwxyz0123456789ABCDEF";
        public List<string> Calls { get; } = [];
        public IReadOnlyList<StoredSessionAccess> SharedWith { get; init; } = [];

        public Task<SessionSharingOverview> ListForOwnerAsync(string owner, string sessionId, CancellationToken ct = default)
        {
            Calls.Add($"list {owner} {sessionId}");
            return Task.FromResult(new SessionSharingOverview([], [], null));
        }

        public Task<IReadOnlyList<StoredSessionAccess>> ListSharedWithAsync(string recipient, CancellationToken ct = default)
        {
            Calls.Add($"shared-with {recipient}");
            return Task.FromResult(SharedWith);
        }

        public Task<DirectSessionShare> UpsertDirectAsync(string owner, string sessionId, string recipient, ShareRole role, CancellationToken ct = default)
        {
            Calls.Add($"upsert {owner} {sessionId} {recipient} {role}");
            return Task.FromResult(new DirectSessionShare(recipient, role, DateTime.UtcNow, DateTime.UtcNow));
        }

        public Task DeleteDirectAsync(string owner, string sessionId, string recipient, CancellationToken ct = default)
        {
            Calls.Add($"delete-direct {owner} {sessionId} {recipient}");
            return Task.CompletedTask;
        }

        public Task<IssuedSessionShareLink> CreateLinkAsync(string owner, string sessionId, ShareRole role, DateTime? expiresAt, CancellationToken ct = default)
        {
            Calls.Add($"create-link {owner} {sessionId} {role}");
            return Task.FromResult(new IssuedSessionShareLink(
                new SessionShareLink("link-1", role, expiresAt, DateTime.UtcNow, DateTime.UtcNow, null), Token));
        }

        public Task<SessionShareLink> UpdateLinkAsync(string owner, string sessionId, string linkId, ShareRole role, DateTime? expiresAt, CancellationToken ct = default)
        {
            Calls.Add($"update-link {owner} {sessionId} {linkId} {role}");
            return Task.FromResult(new SessionShareLink(linkId, role, expiresAt, DateTime.UtcNow, DateTime.UtcNow, null));
        }

        public Task DeleteLinkAsync(string owner, string sessionId, string linkId, CancellationToken ct = default)
        {
            Calls.Add($"delete-link {owner} {sessionId} {linkId}");
            return Task.CompletedTask;
        }

        public Task<SessionMcpPolicy?> SetMcpPolicyAsync(string owner, string sessionId, IReadOnlyCollection<string>? blockedServers, IReadOnlyCollection<string>? blockedTools, CancellationToken ct = default)
        {
            Calls.Add($"policy {owner} {sessionId}");
            return Task.FromResult<SessionMcpPolicy?>(null);
        }
    }
}
