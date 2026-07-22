using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class UserDirectoryChatPostgresTests
{
    [PostgreSqlFact]
    public async Task TelegramLink_RoundTrips()
    {
        await using var database = await PostgresUserDirectoryDatabase.CreateAsync();
        await database.Directory.RecordLoginAsync("maik", "maik@example.com", "Maik");

        await database.Directory.SetTelegramLinkAsync("maik", "chat-123", isForum: true);
        var linked = await database.Directory.GetByTelegramChatAsync("chat-123");

        Assert.NotNull(linked);
        Assert.Equal("maik", linked.Owner);
        Assert.Equal("chat-123", linked.TelegramChatId);
        Assert.True(linked.TelegramForum);

        await database.Directory.ClearTelegramLinkAsync("maik");

        Assert.Null(await database.Directory.GetByTelegramChatAsync("chat-123"));
    }

    [PostgreSqlFact]
    public async Task TelegramLink_SecondUserSteals()
    {
        await using var database = await PostgresUserDirectoryDatabase.CreateAsync();
        await database.Directory.RecordLoginAsync("alice", "alice@example.com", "Alice");
        await database.Directory.RecordLoginAsync("bob", "bob@example.com", "Bob");

        Assert.False(await database.Directory.SetTelegramLinkAsync("alice", "chat-x", isForum: false));
        Assert.True(await database.Directory.SetTelegramLinkAsync("bob", "chat-x", isForum: false));

        var linked = await database.Directory.GetByTelegramChatAsync("chat-x");
        Assert.NotNull(linked);
        Assert.Equal("bob", linked.Owner);

        var alice = await database.Directory.GetAsync("alice");
        Assert.NotNull(alice);
        Assert.Null(alice.TelegramChatId);
    }

    [PostgreSqlFact]
    public async Task SignalNumber_ConflictReturnsFalse()
    {
        await using var database = await PostgresUserDirectoryDatabase.CreateAsync();
        await database.Directory.RecordLoginAsync("alice", "alice@example.com", "Alice");
        await database.Directory.RecordLoginAsync("bob", "bob@example.com", "Bob");

        Assert.True(await database.Directory.SetSignalNumberAsync("alice", "+15550009999"));
        Assert.False(await database.Directory.SetSignalNumberAsync("bob", "+15550009999"));

        var bob = await database.Directory.GetAsync("bob");
        Assert.NotNull(bob);
        Assert.Null(bob.SignalNumber);
    }

    [PostgreSqlFact]
    public async Task SignalNumber_ResaveSameNumber_KeepsVerified()
    {
        await using var database = await PostgresUserDirectoryDatabase.CreateAsync();
        await database.Directory.SetSignalNumberAsync("maik", "+15550001111");
        await database.Directory.SetSignalVerifiedAsync("maik", true);

        Assert.True(await database.Directory.SetSignalNumberAsync("maik", "+15550001111"));

        var user = await database.Directory.GetAsync("maik");
        Assert.NotNull(user);
        Assert.True(user.SignalVerified);
    }

    [PostgreSqlFact]
    public async Task SignalNumberChange_ResetsVerification()
    {
        await using var database = await PostgresUserDirectoryDatabase.CreateAsync();
        await database.Directory.SetSignalNumberAsync("maik", "+15550001111");
        await database.Directory.SetSignalVerifiedAsync("maik", true);

        var verified = await database.Directory.GetAsync("maik");
        Assert.NotNull(verified);
        Assert.True(verified.SignalVerified);

        await database.Directory.SetSignalNumberAsync("maik", "+15550002222");

        var changed = await database.Directory.GetAsync("maik");
        Assert.NotNull(changed);
        Assert.Equal("+15550002222", changed.SignalNumber);
        Assert.False(changed.SignalVerified);
    }

    [PostgreSqlFact]
    public async Task NewColumns_DefaultsAreSane()
    {
        await using var database = await PostgresUserDirectoryDatabase.CreateAsync();
        await database.Directory.RecordLoginAsync("maik", "maik@example.com", "Maik");

        var user = await database.Directory.GetAsync("maik");

        Assert.NotNull(user);
        Assert.True(user.TelegramEnabled);
        Assert.True(user.SignalEnabled);
        Assert.Null(user.TelegramChatId);
        Assert.False(user.TelegramForum);
        Assert.Null(user.SignalNumber);
        Assert.False(user.SignalVerified);
    }
}

internal sealed class PostgresUserDirectoryDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;

    private PostgresUserDirectoryDatabase(string baseConnectionString, string schema, UserDirectory directory)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        Directory = directory;
    }

    public UserDirectory Directory { get; }

    public static async Task<PostgresUserDirectoryDatabase> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");
        }

        var schema = $"userdir_test_{Guid.NewGuid():N}";
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
        var directory = new UserDirectory(configuration);

        try
        {
            await directory.InitializeAsync();
            return new PostgresUserDirectoryDatabase(baseConnectionString, schema, directory);
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
