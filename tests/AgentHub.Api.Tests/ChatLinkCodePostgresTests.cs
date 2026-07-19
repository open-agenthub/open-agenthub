using AgentHub.Api.Chat;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class ChatLinkCodePostgresTests
{
    [PostgreSqlFact]
    public async Task Create_ReplacesPreviousCode()
    {
        await using var database = await PostgresLinkCodeDatabase.CreateAsync();

        var first = await database.Store.CreateAsync("alice", "telegram");
        var second = await database.Store.CreateAsync("alice", "telegram");

        Assert.NotEqual(first, second);
        Assert.Null(await database.Store.ConsumeAsync(first, "telegram"));
        var consumed = await database.Store.ConsumeAsync(second, "telegram");
        Assert.NotNull(consumed);
        Assert.Equal("alice", consumed.Value.Owner);
    }

    [PostgreSqlFact]
    public async Task Consume_IsOneShot()
    {
        await using var database = await PostgresLinkCodeDatabase.CreateAsync();

        var code = await database.Store.CreateAsync("bob", "telegram", payload: "extra");

        var first = await database.Store.ConsumeAsync(code, "telegram");
        Assert.NotNull(first);
        Assert.Equal("bob", first.Value.Owner);
        Assert.Equal("extra", first.Value.Payload);

        Assert.Null(await database.Store.ConsumeAsync(code, "telegram"));
    }

    [PostgreSqlFact]
    public async Task Consume_WrongPurpose_Null()
    {
        await using var database = await PostgresLinkCodeDatabase.CreateAsync();

        var code = await database.Store.CreateAsync("carol", "telegram");

        Assert.Null(await database.Store.ConsumeAsync(code, "signal-verify"));
        // The code stays live for its real purpose.
        Assert.NotNull(await database.Store.ConsumeAsync(code, "telegram"));
    }

    [PostgreSqlFact]
    public async Task SignalVerify_CodeIsSixDigits()
    {
        await using var database = await PostgresLinkCodeDatabase.CreateAsync();

        var code = await database.Store.CreateAsync("dave", "signal-verify");

        Assert.Equal(6, code.Length);
        Assert.All(code, c => Assert.True(char.IsAsciiDigit(c)));
        var value = int.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(value, 100000, 999999);
    }
}

internal sealed class PostgresLinkCodeDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;

    private PostgresLinkCodeDatabase(string baseConnectionString, string schema, ChatLinkCodeStore store)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        Store = store;
    }

    public ChatLinkCodeStore Store { get; }

    public static async Task<PostgresLinkCodeDatabase> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");
        }

        var schema = $"link_codes_test_{Guid.NewGuid():N}";
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
        var store = new ChatLinkCodeStore(configuration);

        try
        {
            await store.InitializeAsync();
            return new PostgresLinkCodeDatabase(baseConnectionString, schema, store);
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
