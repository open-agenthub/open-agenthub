using AgentHub.Api.Chat;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class AccountPurgePostgresTests
{
    [PostgreSqlFact]
    public async Task Purge_RemovesTheOwnersRows_AndKeepsOtherUsers()
    {
        await using var db = await PostgresPurgeDatabase.CreateAsync();

        // alice: account, session (+chat binding/message), api token, project, usage, link code.
        await db.Users.RecordLoginAsync("alice", "alice@example.com", "Alice");
        await db.ExecAsync("""
            INSERT INTO sessions (id, owner, mode, claude_session_id, callback_token, agent_session_id)
            VALUES ('sess-a', 'alice', 'Autonomous', 'c1', 't1', 'c1')
            """);
        await db.Bindings.UpsertAsync(new ChatBinding("telegram", "sess-a", "alice", "chat-1", null, null, true));
        await db.Bindings.RecordMessageAsync("telegram", "chat-1", "42", "sess-a");
        await db.Tokens.CreateAsync("alice", "cli", "oah_secret_alice");
        await db.ExecAsync("INSERT INTO projects (id, owner, name) VALUES ('p1', 'alice', 'Tools')");
        await db.ExecAsync("""
            INSERT INTO session_usage (session_id, owner, cost_usd) VALUES ('sess-a', 'alice', 1.5);
            INSERT INTO usage_monthly (owner, month, cost_usd) VALUES ('alice', date '2026-08-01', 1.5)
            """);
        await db.Codes.CreateAsync("alice", "telegram");

        // bob: control group — must survive untouched.
        await db.Users.RecordLoginAsync("bob", "bob@example.com", "Bob");
        await db.ExecAsync("""
            INSERT INTO sessions (id, owner, mode, claude_session_id, callback_token, agent_session_id)
            VALUES ('sess-b', 'bob', 'Autonomous', 'c2', 't2', 'c2')
            """);
        await db.Tokens.CreateAsync("bob", "cli", "oah_secret_bob");

        var purge = db.CreatePurgeStore();
        var sessionIds = await purge.ListSessionIdsAsync("alice");
        Assert.Equal(new[] { "sess-a" }, sessionIds);

        // EE tables (slack_threads, session_shares, …) and the skills tables were never
        // created in this schema — the purge must tolerate that.
        var skills = await purge.PurgeAsync("alice", sessionIds);
        Assert.Empty(skills);

        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM app_users WHERE owner = 'alice'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM sessions WHERE owner = 'alice'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM chat_session_bindings WHERE owner = 'alice'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM chat_messages WHERE session_id = 'sess-a'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM api_tokens WHERE owner = 'alice'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM projects WHERE owner = 'alice'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM session_usage WHERE owner = 'alice'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM usage_monthly WHERE owner = 'alice'"));
        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM chat_link_codes WHERE owner = 'alice'"));

        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM app_users WHERE owner = 'bob'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM sessions WHERE owner = 'bob'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM api_tokens WHERE owner = 'bob'"));
    }

    [PostgreSqlFact]
    public async Task Purge_IsIdempotent()
    {
        await using var db = await PostgresPurgeDatabase.CreateAsync();
        await db.Users.RecordLoginAsync("alice", "alice@example.com", "Alice");

        var purge = db.CreatePurgeStore();
        await purge.PurgeAsync("alice", await purge.ListSessionIdsAsync("alice"));
        await purge.PurgeAsync("alice", Array.Empty<string>());   // retry after completion

        Assert.Equal(0, await db.CountAsync("SELECT count(*) FROM app_users WHERE owner = 'alice'"));
    }
}

internal sealed class PostgresPurgeDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;
    private readonly IConfiguration _config;
    private readonly NpgsqlDataSource _raw;

    private PostgresPurgeDatabase(string baseConnectionString, string schema, IConfiguration config,
        UserDirectory users, PostgresSessionStore sessions, ChatBindingStore bindings,
        ChatLinkCodeStore codes, ApiTokenStore tokens, PostgresProjectStore projects, PostgresUsageStore usage)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        _config = config;
        _raw = NpgsqlDataSource.Create(config.GetConnectionString("Postgres")!);
        Users = users; Sessions = sessions; Bindings = bindings; Codes = codes;
        Tokens = tokens; Projects = projects; Usage = usage;
    }

    public UserDirectory Users { get; }
    public PostgresSessionStore Sessions { get; }
    public ChatBindingStore Bindings { get; }
    public ChatLinkCodeStore Codes { get; }
    public ApiTokenStore Tokens { get; }
    public PostgresProjectStore Projects { get; }
    public PostgresUsageStore Usage { get; }

    public AccountPurgeStore CreatePurgeStore()
        => new(_config, NullLogger<AccountPurgeStore>.Instance);

    public async Task ExecAsync(string sql)
    {
        await using var cmd = _raw.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> CountAsync(string sql)
    {
        await using var cmd = _raw.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    public static async Task<PostgresPurgeDatabase> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");

        var schema = $"purge_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = builder.ConnectionString
            })
            .Build();

        var users = new UserDirectory(config);
        var sessions = new PostgresSessionStore(config);
        var bindings = new ChatBindingStore(config);
        var codes = new ChatLinkCodeStore(config);
        var tokens = new ApiTokenStore(config);
        var projects = new PostgresProjectStore(config);
        var usage = new PostgresUsageStore(config);
        try
        {
            await users.InitializeAsync();
            await sessions.InitializeAsync();
            await bindings.InitializeAsync();
            await codes.InitializeAsync();
            await tokens.InitializeAsync();
            await projects.InitializeAsync();
            await usage.InitializeAsync();
            return new PostgresPurgeDatabase(baseConnectionString, schema, config,
                users, sessions, bindings, codes, tokens, projects, usage);
        }
        catch
        {
            await DropSchemaAsync(baseConnectionString, schema);
            throw;
        }
    }

    public ValueTask DisposeAsync() => new(DropSchemaAsync(_baseConnectionString, _schema));

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }
}
