using AgentHub.Api.Network;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class PortGrantStorePostgresTests
{
    [PostgreSqlFact]
    public async Task Add_List_Delete_RoundTrip()
    {
        await using var database = await PostgresPortGrantDatabase.CreateAsync();
        var store = database.Store;

        await store.AddAsync("session-a", PortDirection.Egress, 5432, "TCP");
        await store.AddAsync("session-a", PortDirection.BrowserToAgent, 3000, "TCP");
        await store.AddAsync("session-b", PortDirection.Egress, 3306, "TCP");

        var grants = await store.ListAsync("session-a");
        Assert.Equal(2, grants.Count);
        Assert.Equal(PortDirection.Egress, grants[0].Direction);
        Assert.Equal(5432, grants[0].Port);
        Assert.Equal("TCP", grants[0].Protocol);
        Assert.Equal(PortDirection.BrowserToAgent, grants[1].Direction);

        Assert.True(await store.ExistsAsync("session-a", PortDirection.Egress, 5432, "TCP"));
        Assert.False(await store.ExistsAsync("session-a", PortDirection.Egress, 5432, "UDP"));
        Assert.False(await store.ExistsAsync("session-b", PortDirection.Egress, 5432, "TCP"));

        await store.DeleteBySessionAsync("session-a");
        Assert.Empty(await store.ListAsync("session-a"));
        Assert.Single(await store.ListAsync("session-b")); // untouched
    }

    [PostgreSqlFact]
    public async Task Add_IsIdempotent()
    {
        await using var database = await PostgresPortGrantDatabase.CreateAsync();
        var store = database.Store;

        await store.AddAsync("session-a", PortDirection.Egress, 5432, "TCP");
        await store.AddAsync("session-a", PortDirection.Egress, 5432, "tcp"); // normalized

        Assert.Single(await store.ListAsync("session-a"));
    }
}

internal sealed class PostgresPortGrantDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;

    private PostgresPortGrantDatabase(string baseConnectionString, string schema, PortGrantStore store)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        Store = store;
    }

    public PortGrantStore Store { get; }

    public static async Task<PostgresPortGrantDatabase> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");
        }

        var schema = $"portgrants_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = builder.ConnectionString
            })
            .Build();
        var store = new PortGrantStore(configuration);

        try
        {
            await store.InitializeAsync();
            return new PostgresPortGrantDatabase(baseConnectionString, schema, store);
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
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }
}
