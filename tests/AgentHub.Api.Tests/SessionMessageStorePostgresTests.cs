using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionMessageStorePostgresTests
{
    [PostgreSqlFact]
    public async Task Take_ReturnsOldestFirst_AndMarksDelivered()
    {
        await using var database = await PostgresMessageDatabase.CreateAsync();
        await database.Messages.AddAsync(Message("m-1", body: "first"));
        await Task.Delay(10); // created_at has finite resolution — make the ordering strict
        await database.Messages.AddAsync(Message("m-2", body: "second"));

        var taken = await database.Messages.TakeUndeliveredAsync("target", limit: 10);

        Assert.Equal(["m-1", "m-2"], taken.Select(m => m.Id));
        Assert.All(taken, m => Assert.NotNull(m.DeliveredAt));
        Assert.Equal("reviewer", taken[0].FromSessionId);
        Assert.Equal("proj-1", taken[0].ProjectId);
        Assert.Equal("first", taken[0].Body);

        // Delivered messages are never handed out again.
        Assert.Empty(await database.Messages.TakeUndeliveredAsync("target", limit: 10));
    }

    [PostgreSqlFact]
    public async Task Take_HonorsBatchLimit_RestComesNextPoll()
    {
        await using var database = await PostgresMessageDatabase.CreateAsync();
        for (var i = 0; i < 3; i++)
        {
            await database.Messages.AddAsync(Message($"m-{i}"));
            await Task.Delay(5);
        }

        var first = await database.Messages.TakeUndeliveredAsync("target", limit: 2);
        var second = await database.Messages.TakeUndeliveredAsync("target", limit: 2);

        Assert.Equal(["m-0", "m-1"], first.Select(m => m.Id));
        Assert.Equal(["m-2"], second.Select(m => m.Id));
    }

    [PostgreSqlFact]
    public async Task Take_IsScopedToTheTargetSession()
    {
        await using var database = await PostgresMessageDatabase.CreateAsync();
        await database.Messages.AddAsync(Message("mine"));
        await database.Messages.AddAsync(Message("other", to: "someone-else"));

        var taken = await database.Messages.TakeUndeliveredAsync("target", limit: 10);

        Assert.Equal(["mine"], taken.Select(m => m.Id));
    }

    [PostgreSqlFact]
    public async Task ListRecent_ShowsDeliveredAndUndelivered_NewestFirst()
    {
        await using var database = await PostgresMessageDatabase.CreateAsync();
        await database.Messages.AddAsync(Message("m-1"));
        await Task.Delay(10);
        await database.Messages.AddAsync(Message("m-2", from: null)); // external sender
        await database.Messages.TakeUndeliveredAsync("target", limit: 1); // delivers m-1

        var recent = await database.Messages.ListRecentAsync("target", limit: 10);

        Assert.Equal(["m-2", "m-1"], recent.Select(m => m.Id));
        Assert.Null(recent[0].DeliveredAt);
        Assert.Null(recent[0].FromSessionId);
        Assert.NotNull(recent[1].DeliveredAt);
    }

    [PostgreSqlFact]
    public async Task SessionDescription_RoundTrips_AndClears()
    {
        await using var database = await PostgresMessageDatabase.CreateAsync();
        var record = new SessionRecord
        {
            Id = "s-1",
            Owner = "alice",
            Title = "Reviewer",
            Description = "Reviews merge requests of this project.",
            Mode = SessionMode.Interactive,
            AgentSessionId = "agent-1",
            CallbackToken = "tok-1"
        };

        await database.Sessions.UpsertAsync(record);
        var read = await database.Sessions.GetAsync("alice", "s-1");

        Assert.NotNull(read);
        Assert.Equal("Reviews merge requests of this project.", read!.Description);

        read.Description = null;
        await database.Sessions.UpsertAsync(read);

        Assert.Null((await database.Sessions.GetAsync("alice", "s-1"))!.Description);
    }

    private static SessionMessageRecord Message(string id, string? from = "reviewer",
        string to = "target", string body = "please review MR 42") => new()
    {
        Id = id,
        ProjectId = "proj-1",
        FromSessionId = from,
        ToSessionId = to,
        Owner = "alice",
        Body = body
    };
}

internal sealed class PostgresMessageDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;

    private PostgresMessageDatabase(string baseConnectionString, string schema,
        PostgresSessionMessageStore messages, PostgresSessionStore sessions, string connectionString)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        Messages = messages;
        Sessions = sessions;
        ConnectionString = connectionString;
    }

    public PostgresSessionMessageStore Messages { get; }
    public PostgresSessionStore Sessions { get; }
    /// <summary>Scoped to this test's schema, for raw SQL a test needs beside the stores.</summary>
    public string ConnectionString { get; }

    public static async Task<PostgresMessageDatabase> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");
        }

        var schema = $"messages_test_{Guid.NewGuid():N}";
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
        var messages = new PostgresSessionMessageStore(configuration);
        var sessions = new PostgresSessionStore(configuration);

        try
        {
            await messages.InitializeAsync();
            await sessions.InitializeAsync();
            return new PostgresMessageDatabase(baseConnectionString, schema, messages, sessions, builder.ConnectionString);
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
