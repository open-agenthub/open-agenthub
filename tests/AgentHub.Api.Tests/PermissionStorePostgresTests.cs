using AgentHub.Api.Permissions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class PermissionStorePostgresTests
{
    [PostgreSqlFact]
    public async Task SetPromptMessage_RoundTripsThroughGet()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        await database.Store.CreateAsync(NewRequest("req-1", "session-a"));

        await database.Store.SetPromptMessageAsync("req-1", "slack", "C123", "1700000000.000100");
        var read = await database.Store.GetAsync("req-1");

        Assert.NotNull(read);
        Assert.Equal("slack", read.Platform);
        Assert.Equal("C123", read.Channel);
        Assert.Equal("1700000000.000100", read.MessageTs);
        Assert.Null(read.Decision);
    }

    [PostgreSqlFact]
    public async Task Resolve_SetsTheDecisionOnlyOnce()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        await database.Store.CreateAsync(NewRequest("req-1", "session-a"));

        var first = await database.Store.ResolveAsync("req-1", "allow");
        var second = await database.Store.ResolveAsync("req-1", "deny");

        Assert.NotNull(first);
        Assert.Equal("allow", first.Decision);
        Assert.Null(second);
        Assert.Equal("allow", await database.Store.GetDecisionAsync("req-1"));
    }

    [PostgreSqlFact]
    public async Task Resolve_IsScopedToItsSession()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        await database.Store.CreateAsync(NewRequest("req-1", "session-a"));

        Assert.Null(await database.Store.ResolveAsync("req-1", "allow", sessionId: "session-b"));
        Assert.Null(await database.Store.GetDecisionAsync("req-1", sessionId: "session-b"));
        Assert.Null(await database.Store.GetAsync("req-1", sessionId: "session-b"));

        var resolved = await database.Store.ResolveAsync("req-1", "allow", sessionId: "session-a");

        Assert.NotNull(resolved);
        Assert.Equal("allow", resolved.Decision);
    }

    [PostgreSqlFact]
    public async Task Expire_ShowsUpAsTheDecision()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        await database.Store.CreateAsync(NewRequest("req-1", "session-a"));

        var expired = await database.Store.ResolveAsync("req-1", "expired");

        Assert.NotNull(expired);
        Assert.Equal("expired", await database.Store.GetDecisionAsync("req-1"));
    }

    [PostgreSqlFact]
    public async Task GetPendingBySession_ReturnsNewestUndecided()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        await database.Store.CreateAsync(NewRequest("req-1", "session-a", tool: "Bash"));
        await Task.Delay(10); // created_at has finite resolution — make the ordering strict
        await database.Store.CreateAsync(NewRequest("req-2", "session-a", tool: "WebFetch"));

        // Both pending — the newest one wins.
        Assert.Equal("WebFetch", await database.Store.GetPendingBySessionAsync("session-a"));

        await database.Store.ResolveAsync("req-2", "allow");

        Assert.Equal("Bash", await database.Store.GetPendingBySessionAsync("session-a"));

        await database.Store.ResolveAsync("req-1", "deny");

        Assert.Null(await database.Store.GetPendingBySessionAsync("session-a"));
    }

    [PostgreSqlFact]
    public async Task ExpireStale_OnlyAffectsOldPending()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        await database.Store.CreateAsync(NewRequest("old-pending", "session-a"));
        await database.Store.CreateAsync(NewRequest("fresh-pending", "session-a"));
        await database.Store.CreateAsync(NewRequest("old-decided", "session-a"));
        await database.Store.ResolveAsync("old-decided", "allow");
        await database.ExecuteSqlAsync(
            "UPDATE permission_requests SET created_at = now() - interval '40 minutes' WHERE id IN ('old-pending', 'old-decided')");

        var expired = await database.Store.ExpireStaleAsync(TimeSpan.FromMinutes(35));

        var row = Assert.Single(expired);
        Assert.Equal("old-pending", row.Id);
        Assert.Equal("expired", row.Decision);
        Assert.Equal("expired", await database.Store.GetDecisionAsync("old-pending"));
        Assert.Null(await database.Store.GetDecisionAsync("fresh-pending"));   // still pending
        Assert.Equal("allow", await database.Store.GetDecisionAsync("old-decided")); // untouched
    }

    private static PermissionRequest NewRequest(string id, string sessionId, string tool = "Bash") => new()
    {
        Id = id,
        SessionId = sessionId,
        Owner = "owner-a",
        Tool = tool,
        Summary = "ls"
    };
}

internal sealed class PostgresPermissionDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;

    private PostgresPermissionDatabase(string baseConnectionString, string schema, PermissionStore store)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        Store = store;
    }

    public PermissionStore Store { get; }

    public static async Task<PostgresPermissionDatabase> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");
        }

        var schema = $"permissions_test_{Guid.NewGuid():N}";
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
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = builder.ConnectionString
            })
            .Build();
        var store = new PermissionStore(configuration);

        try
        {
            await store.InitializeAsync();
            return new PostgresPermissionDatabase(baseConnectionString, schema, store);
        }
        catch
        {
            await DropSchemaAsync(baseConnectionString, schema);
            throw;
        }
    }

    /// <summary>Raw SQL against the test schema — for backdating rows and similar fixture tweaks.</summary>
    public async Task ExecuteSqlAsync(string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = _schema };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
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
