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
        await database.Store.CreateAsync(NewRequest("req-2", "session-a", tool: "WebFetch"));

        await database.Store.ResolveAsync("req-2", "allow");

        Assert.Equal("Bash", await database.Store.GetPendingBySessionAsync("session-a"));

        await database.Store.ResolveAsync("req-1", "deny");

        Assert.Null(await database.Store.GetPendingBySessionAsync("session-a"));
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
