using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionShareStorePostgresTests
{
    [PostgreSqlFact]
    public async Task CrossOwnerMutations_AreRejected()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("owner-a", "session-a");
        await database.AddUserAsync("recipient");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Shares.UpsertDirectAsync(
                "owner-b",
                "session-a",
                "recipient",
                ShareRole.Viewer));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Shares.SetMcpPolicyAsync(
                "owner-b",
                "session-a",
                ["blocked-server"],
                []));
    }

    [PostgreSqlFact]
    public async Task DirectShare_RequiresKnownNonOwnerRecipient()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("owner-a", "session-a");
        await database.AddUserAsync("owner-a");

        var unknown = await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Shares.UpsertDirectAsync(
                "owner-a",
                "session-a",
                "unknown-user",
                ShareRole.Viewer));
        var selfShare = await Assert.ThrowsAsync<ArgumentException>(() =>
            database.Shares.UpsertDirectAsync(
                "owner-a",
                "session-a",
                "owner-a",
                ShareRole.Collaborator));

        Assert.Contains("known user", unknown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("themselves", selfShare.Message, StringComparison.OrdinalIgnoreCase);
    }

    [PostgreSqlFact]
    public async Task LinkCrud_IsScopedToItsSession()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("owner-a", "session-a");
        await database.AddSessionAsync("owner-a", "session-b");
        var issued = await database.Shares.CreateLinkAsync(
            "owner-a",
            "session-a",
            ShareRole.Viewer,
            DateTime.UtcNow.AddHours(1));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Shares.UpdateLinkAsync(
                "owner-a",
                "session-b",
                issued.Link.Id,
                ShareRole.Collaborator,
                DateTime.UtcNow.AddHours(2)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            database.Shares.DeleteLinkAsync(
                "owner-a",
                "session-b",
                issued.Link.Id));

        var updated = await database.Shares.UpdateLinkAsync(
            "owner-a",
            "session-a",
            issued.Link.Id,
            ShareRole.Collaborator,
            DateTime.UtcNow.AddHours(2));
        Assert.Equal(ShareRole.Collaborator, updated.Role);

        await database.Shares.DeleteLinkAsync("owner-a", "session-a", issued.Link.Id);
        var overview = await database.Shares.ListForOwnerAsync("owner-a", "session-a");
        Assert.Empty(overview.Links);
    }

    [PostgreSqlFact]
    public async Task ExpiredAndRevokedLinks_DoNotResolve()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("owner-a", "session-a");

        var expired = await database.Shares.CreateLinkAsync(
            "owner-a",
            "session-a",
            ShareRole.Viewer,
            DateTime.UtcNow.AddHours(1));
        await database.ExecuteAsync(
            "UPDATE session_share_links SET expires_at = now() - interval '1 minute' WHERE id = @id",
            new NpgsqlParameter("id", expired.Link.Id));

        Assert.Null(await database.Shares.FindTokenAccessAsync(expired.Token));

        var revoked = await database.Shares.CreateLinkAsync(
            "owner-a",
            "session-a",
            ShareRole.Collaborator,
            null);
        Assert.NotNull(await database.Shares.FindTokenAccessAsync(revoked.Token));

        await database.Shares.DeleteLinkAsync("owner-a", "session-a", revoked.Link.Id);

        Assert.Null(await database.Shares.FindTokenAccessAsync(revoked.Token));
    }

    [PostgreSqlFact]
    public async Task LastUsedUpdateFailure_DoesNotDenyValidLink()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("owner-a", "session-a");
        var issued = await database.Shares.CreateLinkAsync(
            "owner-a",
            "session-a",
            ShareRole.Viewer,
            null);
        await database.ExecuteAsync(
            """
            CREATE FUNCTION reject_last_used_update() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'last_used_at write rejected for test';
            END;
            $$;
            CREATE TRIGGER reject_last_used_update
            BEFORE UPDATE OF last_used_at ON session_share_links
            FOR EACH ROW EXECUTE FUNCTION reject_last_used_update();
            """);

        var resolved = await database.Shares.FindTokenAccessAsync(issued.Token);
        var lastUsed = await database.ScalarAsync<DateTime?>(
            "SELECT last_used_at FROM session_share_links WHERE id = @id",
            new NpgsqlParameter("id", issued.Link.Id));

        Assert.NotNull(resolved);
        Assert.Equal(ShareRole.Viewer, resolved.Role);
        Assert.Null(lastUsed);
    }

    [PostgreSqlFact]
    public async Task DeleteForSession_RemovesAllSharingRows()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("owner-a", "session-a");
        await database.AddUserAsync("recipient");
        await database.Shares.UpsertDirectAsync(
            "owner-a",
            "session-a",
            "recipient",
            ShareRole.Viewer);
        await database.Shares.CreateLinkAsync(
            "owner-a",
            "session-a",
            ShareRole.Collaborator,
            null);
        await database.Shares.SetMcpPolicyAsync(
            "owner-a",
            "session-a",
            ["blocked-server"],
            ["mcp__blocked-server__tool"]);

        await database.Shares.DeleteForSessionAsync("session-a");

        var remaining = await database.ScalarAsync<long>(
            """
            SELECT
                (SELECT count(*) FROM session_shares WHERE session_id = @session)
              + (SELECT count(*) FROM session_share_links WHERE session_id = @session)
              + (SELECT count(*) FROM session_mcp_policies WHERE session_id = @session)
            """,
            new NpgsqlParameter("session", "session-a"));
        Assert.Equal(0, remaining);
    }
    [PostgreSqlFact]
    public async Task SessionStore_MigratesAndRoundTripsAgentConfiguration()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var policy = "{\"allowedTools\":[\"Read\"],\"allowedMcpTools\":[\"mcp__git\"],\"allowedCommands\":[\"git status\"]}";
        var record = new SessionRecord
        {
            Id = "agent-session", Owner = "alice", Title = "Codex", Mode = SessionMode.Autonomous,
            Agent = AgentKind.Codex, AuthMode = AgentAuthMode.ApiKey, AgentPolicyJson = policy,
            AgentSessionId = "thread-1", CallbackToken = "callback-1"
        };

        await database.UpsertSessionAsync(record);
        var stored = await database.GetSessionAsync("alice", "agent-session");
        var agentDefault = await database.ScalarAsync<string>(
            "SELECT column_default FROM information_schema.columns WHERE table_name = 'sessions' AND column_name = 'agent'");
        var authDefault = await database.ScalarAsync<string>(
            "SELECT column_default FROM information_schema.columns WHERE table_name = 'sessions' AND column_name = 'auth_mode'");

        Assert.NotNull(stored);
        Assert.Equal(AgentKind.Codex, stored!.Agent);
        Assert.Equal(AgentAuthMode.ApiKey, stored.AuthMode);
        Assert.Equal("thread-1", stored.AgentSessionId);
        Assert.Equal(policy, stored.AgentPolicyJson);
        Assert.Contains("Claude", agentDefault);
        Assert.Contains("Auto", authDefault);
    }

    [PostgreSqlFact]
    public async Task OwnerAccess_MapsProviderNeutralSessionId_WhenLegacyClaudeIdIsNull()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var record = new SessionRecord
        {
            Id = "codex-owner-session",
            Owner = "owner-a",
            Title = "Codex",
            Mode = SessionMode.Interactive,
            Agent = AgentKind.Codex,
            AuthMode = AgentAuthMode.Subscription,
            AgentSessionId = "codex-thread",
            CallbackToken = "synthetic-callback"
        };

        await database.UpsertSessionAsync(record);
        var access = await database.Shares.FindUserAccessAsync(record.Owner, record.Id);

        Assert.NotNull(access);
        Assert.Equal("codex-thread", access!.Session.AgentSessionId);
        Assert.Null(access.Session.ClaudeSessionId);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public const string ConnectionStringEnvironmentVariable =
        "AGENTHUB_TEST_POSTGRES_CONNECTION_STRING";

    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)))
        {
            Skip = $"Set {ConnectionStringEnvironmentVariable} to run PostgreSQL integration tests.";
        }
    }
}

internal sealed class PostgresSharingDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;
    private readonly string _connectionString;
    private readonly PostgresSessionStore _sessions;
    private readonly UserDirectory _users;

    private PostgresSharingDatabase(
        string baseConnectionString,
        string schema,
        string connectionString,
        SessionShareStore shares,
        PostgresSessionStore sessions,
        UserDirectory users)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        _connectionString = connectionString;
        Shares = shares;
        _sessions = sessions;
        _users = users;
    }

    public SessionShareStore Shares { get; }

    public static async Task<PostgresSharingDatabase> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");
        }

        var schema = $"sharing_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schema
        };
        var connectionString = builder.ConnectionString;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString
            })
            .Build();
        var sessions = new PostgresSessionStore(configuration);
        var users = new UserDirectory(configuration);
        var shares = new SessionShareStore(
            configuration,
            NullLogger<SessionShareStore>.Instance);

        try
        {
            await sessions.InitializeAsync();
            await users.InitializeAsync();
            await shares.InitializeAsync();
            return new PostgresSharingDatabase(
                baseConnectionString,
                schema,
                connectionString,
                shares,
                sessions,
                users);
        }
        catch
        {
            await DropSchemaAsync(baseConnectionString, schema);
            throw;
        }
    }

    public Task AddSessionAsync(string owner, string id)
        => _sessions.UpsertAsync(new SessionRecord
        {
            Id = id,
            Owner = owner,
            Title = id,
            Mode = SessionMode.Interactive,
            ClaudeSessionId = $"claude-{id}",
            CallbackToken = $"callback-{id}"
        });

    public Task UpsertSessionAsync(SessionRecord record) => _sessions.UpsertAsync(record);

    public Task<SessionRecord?> GetSessionAsync(string owner, string id) => _sessions.GetAsync(owner, id);
    public Task AddUserAsync(string owner)
        => _users.RecordLoginAsync(owner, $"{owner}@example.test", owner);

    public async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        if (result is null || result is DBNull)
            return default!;
        return (T)result;
    }

    public ValueTask DisposeAsync()
        => new(DropSchemaAsync(_baseConnectionString, _schema));

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE",
            connection);
        await command.ExecuteNonQueryAsync();
    }
}
