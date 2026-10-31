using AgentHub.Api.Ee.Slack;
using AgentHub.Api.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionMetadataDeletionPostgresTests
{
    internal static readonly string[] Tables = ["session_shares", "session_share_links", "session_mcp_policies", "slack_threads"];

    [PostgreSqlFact]
    public async Task Migration_PrunesLegacyOrphans_PreservesLiveRows_AndIsRepeatable()
    {
        await using var db = await PostgresSharingDatabase.CreateAsync();
        var threads = new SlackThreadStore(db.Configuration);
        await threads.InitializeAsync();
        foreach (var table in Tables)
            await db.ExecuteAsync($"ALTER TABLE {table} DROP CONSTRAINT {table}_session_fk");
        await db.AddSessionAsync("owner", "live");
        await SeedAsync(db, "live");
        await SeedAsync(db, "gone");

        await db.Shares.InitializeAsync();
        await threads.InitializeAsync();
        await db.Shares.InitializeAsync();
        await threads.InitializeAsync();

        foreach (var table in Tables)
        {
            Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE session_id = 'live'"));
            Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE session_id = 'gone'"));
        }
        await db.Sessions.DeleteAsync("live");
        await AssertEmptyAsync(db, "live");
    }

    [PostgreSqlFact]
    public async Task FailedSessionDelete_RollsBackMetadata_AndNewOrphansAreRejected()
    {
        await using var db = await PostgresSharingDatabase.CreateAsync();
        await new SlackThreadStore(db.Configuration).InitializeAsync();
        await db.AddSessionAsync("owner", "live");
        await SeedAsync(db, "live");
        await db.ExecuteAsync("BEGIN; DELETE FROM sessions WHERE id = 'live'; ROLLBACK;");
        foreach (var table in Tables)
            Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE session_id = 'live'"));
        foreach (var table in Tables)
        {
            var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                db.ExecuteAsync($"UPDATE {table} SET session_id = 'gone' WHERE session_id = 'live'"));
            Assert.Equal("23503", error.SqlState);
        }
    }

    internal static Task SeedAsync(PostgresSharingDatabase db, string id) => db.ExecuteAsync("""
        INSERT INTO session_shares (session_id, recipient_owner, role) VALUES (@id, 'recipient', 'Viewer');
        INSERT INTO session_share_links (id, session_id, token_hash, role) VALUES (@id, @id, convert_to(@id, 'UTF8'), 'Viewer');
        INSERT INTO session_mcp_policies (session_id, blocked_servers, blocked_tools) VALUES (@id, '[]', '[]');
        INSERT INTO slack_threads (session_id, owner, channel, thread_ts) VALUES (@id, 'owner', 'channel', @id);
        """, new Npgsql.NpgsqlParameter("id", id));

    internal static async Task AssertEmptyAsync(PostgresSharingDatabase db, string id)
    {
        Assert.Null(await db.Sessions.GetByIdAsync(id));
        foreach (var table in Tables)
            Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE session_id = @id", new Npgsql.NpgsqlParameter("id", id)));
    }
}
