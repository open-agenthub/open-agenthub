using AgentHub.Api.Ee.Sharing;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>The service against the real store: the one path the fakes cannot prove — that the
/// store's exceptions are the ones the service and its callers translate.</summary>
public class SessionSharingServicePostgresTests
{
    [PostgreSqlFact]
    public async Task ShareThenListSharedWith_ShowsTheSessionToTheRecipientOnly()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("owner-a", "session-a");
        await database.AddSessionAsync("owner-a", "session-b");
        await database.AddUserAsync("recipient");
        await database.AddUserAsync("bystander");
        var service = new SessionSharingService(database.Shares, new FakeEnterpriseLicense(true), "https://hub.example.com");

        await service.ShareWithUserAsync("owner-a", "session-a", "recipient", ShareRole.Collaborator);

        var mine = await service.ListSharedWithAsync("recipient");
        var shared = Assert.Single(mine);
        Assert.Equal("session-a", shared.Id);
        Assert.Equal("Collaborator", shared.AccessRole);
        Assert.Equal("owner-a", shared.SharedBy);
        Assert.True(shared.CanWrite);
        Assert.False(shared.CanManage);
        Assert.Empty(await service.ListSharedWithAsync("bystander"));
        // The owner's own sessions are never "shared with" the owner.
        Assert.Empty(await service.ListSharedWithAsync("owner-a"));

        await service.UnshareUserAsync("owner-a", "session-a", "recipient");
        Assert.Empty(await service.ListSharedWithAsync("recipient"));
    }

    [PostgreSqlFact]
    public async Task UnknownRecipient_SurfacesAsItsOwnExceptionType()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("owner-a", "session-a");
        var service = new SessionSharingService(database.Shares, new FakeEnterpriseLicense(true), null);

        var error = await Assert.ThrowsAsync<UnknownRecipientException>(() =>
            service.ShareWithUserAsync("owner-a", "session-a", "ghost", ShareRole.Viewer));

        Assert.Equal("ghost", error.Recipient);
        // Still an ArgumentException, so every existing 400 mapping keeps catching it.
        Assert.IsAssignableFrom<ArgumentException>(error);
    }

    [PostgreSqlFact]
    public async Task CreateLink_ResolvesThroughTheAbsoluteUrlItReturned()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("owner-a", "session-a");
        var service = new SessionSharingService(database.Shares, new FakeEnterpriseLicense(true), "https://hub.example.com/");

        var created = await service.CreateLinkAsync("owner-a", "session-a", ShareRole.Viewer, DateTime.UtcNow.AddHours(1));

        Assert.StartsWith("https://hub.example.com/shared/", created.Url);
        var token = created.Url["https://hub.example.com/shared/".Length..];
        var access = await database.Shares.FindTokenAccessAsync(token);
        Assert.NotNull(access);
        Assert.Equal("session-a", access!.Session.Id);
        Assert.Equal(ShareRole.Viewer, access.Role);

        await service.DeleteLinkAsync("owner-a", "session-a", created.Link.Id);
        Assert.Null(await database.Shares.FindTokenAccessAsync(token));
    }

    [PostgreSqlFact]
    public async Task AnotherOwnersSession_IsNotFound_NotForbidden()
    {
        // One answer for "not yours" and "does not exist", so a token holder cannot enumerate
        // other owners' session ids by the difference.
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("owner-a", "session-a");
        await database.AddUserAsync("recipient");
        var service = new SessionSharingService(database.Shares, new FakeEnterpriseLicense(true), null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ListAsync("owner-b", "session-a"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ShareWithUserAsync("owner-b", "session-a", "recipient", ShareRole.Viewer));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CreateLinkAsync("owner-b", "session-a", ShareRole.Viewer, null));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.ListAsync("owner-a", "no-such-session"));
    }
}
