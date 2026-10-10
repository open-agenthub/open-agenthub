using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The project file rule against the real session and share tables. The in-memory tests prove
/// the rule; these prove the two queries it stands on answer what it assumes.
/// </summary>
public sealed class ProjectFilesPostgresTests
{
    [PostgreSqlFact]
    public async Task Siblings_come_from_the_caller_s_owner_and_project_only()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var caller = await AddAsync(database, "caller", "alice", "p1");
        await AddAsync(database, "sibling", "alice", "p1");
        await AddAsync(database, "other-project", "alice", "p2");
        await AddAsync(database, "no-project", "alice", null);
        await AddAsync(database, "other-owner", "bob", "p1");
        await AddAsync(database, "deleted", "alice", "p1");
        await database.Sessions.DeleteAsync("deleted");
        var access = new ProjectFileAccess(database.Sessions, database.Shares);

        var siblings = await access.SiblingsAsync(caller);

        Assert.Equal(["sibling"], siblings.Select(session => session.Id));
        Assert.NotNull(await access.ResolveSiblingAsync(caller, "sibling"));
        foreach (var refused in new[]
                 {
                     "other-project", "no-project", "other-owner", "deleted", "caller", "never-existed",
                 })
        {
            Assert.Null(await access.ResolveSiblingAsync(caller, refused));
        }
    }

    [PostgreSqlFact]
    public async Task A_direct_share_of_either_role_closes_the_caller_s_project_access()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var caller = await AddAsync(database, "caller", "alice", "p1");
        await AddAsync(database, "sibling", "alice", "p1");
        await database.AddUserAsync("carol");
        var access = new ProjectFileAccess(database.Sessions, database.Shares);
        Assert.False(await database.Shares.IsSharedAsync("caller"));

        await database.Shares.UpsertDirectAsync("alice", "caller", "carol", ShareRole.Viewer);

        Assert.True(await database.Shares.IsSharedAsync("caller"));
        Assert.Empty(await access.SiblingsAsync(caller));
        Assert.Null(await access.ResolveSiblingAsync(caller, "sibling"));

        // Revoking the share reopens it for the pod that is already running: nothing is cached.
        await database.Shares.DeleteDirectAsync("alice", "caller", "carol");

        Assert.False(await database.Shares.IsSharedAsync("caller"));
        Assert.NotNull(await access.ResolveSiblingAsync(caller, "sibling"));
    }

    [PostgreSqlFact]
    public async Task A_live_link_counts_as_a_share_and_an_expired_or_deleted_one_does_not()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await AddAsync(database, "caller", "alice", "p1");
        await AddAsync(database, "sibling", "alice", "p1");

        var issued = await database.Shares.CreateLinkAsync(
            "alice", "caller", ShareRole.Viewer, DateTime.UtcNow.AddHours(1));

        Assert.True(await database.Shares.IsSharedAsync("caller"));
        // A share of one session says nothing about the sessions next to it.
        Assert.False(await database.Shares.IsSharedAsync("sibling"));

        await database.ExecuteAsync(
            "UPDATE session_share_links SET expires_at = now() - interval '1 minute'");
        Assert.False(await database.Shares.IsSharedAsync("caller"));

        await database.ExecuteAsync("UPDATE session_share_links SET expires_at = NULL");
        Assert.True(await database.Shares.IsSharedAsync("caller"));

        await database.Shares.DeleteLinkAsync("alice", "caller", issued.Link.Id);
        Assert.False(await database.Shares.IsSharedAsync("caller"));
    }

    private static async Task<SessionRecord> AddAsync(
        PostgresSharingDatabase database, string id, string owner, string? project)
    {
        var record = new SessionRecord
        {
            Id = id,
            Owner = owner,
            ProjectId = project,
            Title = id,
            Mode = SessionMode.Interactive,
            CallbackToken = $"callback-{id}",
        };
        await database.UpsertSessionAsync(record);
        return record;
    }
}
