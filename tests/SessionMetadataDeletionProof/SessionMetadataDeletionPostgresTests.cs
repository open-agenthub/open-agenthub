using AgentHub.Api.Ee.Slack;
using AgentHub.Api.Chat;
using Microsoft.Extensions.Configuration;
using Npgsql;
using AgentHub.Api.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionMetadataDeletionPostgresTests
{
    internal static readonly string[] Tables = ["session_shares", "session_share_links", "session_mcp_policies", "slack_threads"];

    [PostgreSqlFact]
    public async Task Migration_PrunesLegacyOrphans_PreservesLiveRows_AndIsRepeatable()
    {
        await using var db = await CreateDatabaseAsync();
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
        Assert.True(await threads.IsDeletedThreadAsync("channel", "gone"));
        Assert.Equal("owner", await db.ScalarAsync<string>(
            "SELECT owner FROM chat_deleted_sessions WHERE platform = 'slack' AND session_id = 'gone'"));
        await db.Sessions.DeleteAsync("live");
        await AssertEmptyAsync(db, "live");
    }

    [PostgreSqlFact]
    public async Task FailedMigration_RollsBackPruningAndConstraints_ThenRetries()
    {
        await using var db = await CreateDatabaseAsync();
        await new SlackThreadStore(db.Configuration).InitializeAsync();
        foreach (var table in Tables)
            await db.ExecuteAsync($"ALTER TABLE {table} DROP CONSTRAINT {table}_session_fk");
        await SeedAsync(db, "gone");
        await db.ExecuteAsync("""
            CREATE FUNCTION refuse_prune() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'prune unavailable'; END $$;
            CREATE TRIGGER refuse_prune BEFORE DELETE ON session_share_links
                FOR EACH ROW EXECUTE FUNCTION refuse_prune();
            """);
        await Assert.ThrowsAsync<PostgresException>(() => db.Shares.InitializeAsync());
        foreach (var table in Tables)
            Assert.Equal(1L, await db.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE session_id = 'gone'"));
        Assert.Equal(0L, await db.ScalarAsync<long>("""
            SELECT count(*) FROM pg_constraint
            WHERE connamespace = current_schema()::regnamespace AND conname LIKE '%_session_fk'
            """));
        await db.ExecuteAsync("DROP TRIGGER refuse_prune ON session_share_links");
        await db.Shares.InitializeAsync();
        await new SlackThreadStore(db.Configuration).InitializeAsync();
        await AssertEmptyAsync(db, "gone");
    }

    [PostgreSqlFact]
    public async Task ReplicaInitialization_SerializesLegacyMigration()
    {
        await using var db = await CreateDatabaseAsync();
        var threads = new SlackThreadStore(db.Configuration);
        await threads.InitializeAsync();
        foreach (var table in Tables)
            await db.ExecuteAsync($"ALTER TABLE {table} DROP CONSTRAINT {table}_session_fk");
        await SeedAsync(db, "gone");
        await Task.WhenAll(db.Shares.InitializeAsync(), db.Shares.InitializeAsync(),
            threads.InitializeAsync(), threads.InitializeAsync()).WaitAsync(TimeSpan.FromSeconds(15));
        await AssertEmptyAsync(db, "gone");
        Assert.Equal(4L, await db.ScalarAsync<long>("""
            SELECT count(*) FROM pg_constraint
            WHERE connamespace = current_schema()::regnamespace AND conname LIKE '%_session_fk'
            """));
    }

    [PostgreSqlFact]
    public async Task InsertRacingDelete_WaitsForRegistryCommit_ThenRejectsEveryMetadataKind()
    {
        await using var db = await CreateDatabaseAsync();
        await new SlackThreadStore(db.Configuration).InitializeAsync();
        var inserts = new[]
        {
            "INSERT INTO session_shares (session_id, recipient_owner, role) VALUES ('live', 'recipient', 'Viewer')",
            "INSERT INTO session_share_links (id, session_id, token_hash, role) VALUES ('link', 'live', decode('01', 'hex'), 'Viewer')",
            "INSERT INTO session_mcp_policies (session_id, blocked_servers, blocked_tools) VALUES ('live', '[]', '[]')",
            "INSERT INTO slack_threads (session_id, owner, channel, thread_ts) VALUES ('live', 'owner', 'channel', 'thread')"
        };
        foreach (var insert in inserts)
        {
            await db.AddSessionAsync("owner", "live");
            await using var deleting = new NpgsqlConnection(db.Configuration.GetConnectionString("Postgres"));
            await using var writing = new NpgsqlConnection(db.Configuration.GetConnectionString("Postgres"));
            await deleting.OpenAsync();
            await writing.OpenAsync();
            await using var transaction = await deleting.BeginTransactionAsync();
            await using (var delete = new NpgsqlCommand("DELETE FROM sessions WHERE id = 'live'", deleting, transaction))
                await delete.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(insert, writing);
            var pending = command.ExecuteNonQueryAsync();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                // Observe the server lock rather than assuming an insert started after a fixed delay.
                while (!await db.ScalarAsync<bool>("SELECT cardinality(pg_blocking_pids(@pid)) > 0",
                    new NpgsqlParameter("pid", writing.ProcessID)))
                    await Task.Delay(10, deadline.Token);
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                await transaction.CommitAsync();
            }
            var error = await Assert.ThrowsAsync<PostgresException>(async () => await pending);
            Assert.Equal("23503", error.SqlState);
            await AssertEmptyAsync(db, "live");
        }
    }

    [PostgreSqlFact]
    public async Task FailedSessionDelete_RollsBackMetadata_AndNewOrphansAreRejected()
    {
        await using var db = await CreateDatabaseAsync();
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

    internal static async Task<PostgresSharingDatabase> CreateDatabaseAsync()
    {
        var db = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await new ChatBindingStore(db.Configuration).InitializeAsync();
        return db;
    }

    internal static Task SeedAsync(PostgresSharingDatabase db, string id) => db.ExecuteAsync("""
        INSERT INTO session_shares (session_id, recipient_owner, role) VALUES (@id, 'recipient', 'Viewer');
        INSERT INTO session_share_links (id, session_id, token_hash, role) VALUES (@id, @id, convert_to(@id, 'UTF8'), 'Viewer');
        INSERT INTO session_mcp_policies (session_id, blocked_servers, blocked_tools) VALUES (@id, '[]', '[]');
        INSERT INTO slack_threads (session_id, owner, channel, thread_ts) VALUES (@id, COALESCE((SELECT owner FROM sessions WHERE id = @id), 'owner'), 'channel', @id);
        """, new Npgsql.NpgsqlParameter("id", id));

    internal static async Task AssertEmptyAsync(PostgresSharingDatabase db, string id)
    {
        Assert.Null(await db.Sessions.GetByIdAsync(id));
        foreach (var table in Tables)
            Assert.Equal(0L, await db.ScalarAsync<long>($"SELECT count(*) FROM {table} WHERE session_id = @id", new Npgsql.NpgsqlParameter("id", id)));
    }
}
