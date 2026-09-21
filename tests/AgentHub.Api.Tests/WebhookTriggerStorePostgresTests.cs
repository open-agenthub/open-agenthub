using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class WebhookTriggerStorePostgresTests
{
    [PostgreSqlFact]
    public async Task Create_Get_List_RoundTrip()
    {
        await using var database = await WebhookTriggerDatabase.CreateAsync();

        var created = await database.Store.CreateAsync("alice", new CreateWebhookTriggerRequest
        {
            Name = "review-bot",
            ProviderId = "gitlab",
            Events = ["opened", "reopened"],
            RepoFilter = "group/demo",
            PromptTemplate = "Review {{title}}",
            ProjectId = "proj-1",
            Agent = AgentKind.Codex,
            AutoApprove = true
        }, "protected:secret");

        var record = await database.Store.GetByIdAsync(created.Id);
        Assert.NotNull(record);
        Assert.Equal("alice", record.Owner);
        Assert.Equal("protected:secret", record.SecretProtected);
        Assert.Equal("review-bot", record.Info.Name);
        Assert.Equal("gitlab", record.Info.ProviderId);
        Assert.Equal(new[] { "opened", "reopened" }, record.Info.Events);
        Assert.Equal("group/demo", record.Info.RepoFilter);
        Assert.Equal("Review {{title}}", record.Info.PromptTemplate);
        Assert.Equal("proj-1", record.Info.ProjectId);
        Assert.Equal(AgentKind.Codex, record.Info.Agent);
        Assert.True(record.Info.AutoApprove);
        Assert.Null(record.Info.LastTriggeredAt);

        var listed = Assert.Single(await database.Store.ListAsync("alice"));
        Assert.Equal(created.Id, listed.Id);
        Assert.Empty(await database.Store.ListAsync("bob"));
    }

    [PostgreSqlFact]
    public async Task Touch_StampsLastTriggeredAt()
    {
        await using var database = await WebhookTriggerDatabase.CreateAsync();
        var created = await database.Store.CreateAsync("alice",
            new CreateWebhookTriggerRequest { Name = "t", PromptTemplate = "p" }, "protected:x");

        await database.Store.TouchAsync(created.Id);

        var record = await database.Store.GetByIdAsync(created.Id);
        Assert.NotNull(record?.Info.LastTriggeredAt);
    }

    [PostgreSqlFact]
    public async Task Delete_IsScopedToOwner()
    {
        await using var database = await WebhookTriggerDatabase.CreateAsync();
        var created = await database.Store.CreateAsync("alice",
            new CreateWebhookTriggerRequest { Name = "t", PromptTemplate = "p" }, "protected:x");

        Assert.False(await database.Store.DeleteAsync("bob", created.Id));
        Assert.NotNull(await database.Store.GetByIdAsync(created.Id));
        Assert.True(await database.Store.DeleteAsync("alice", created.Id));
        Assert.Null(await database.Store.GetByIdAsync(created.Id));
    }
}

internal sealed class WebhookTriggerDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;

    private WebhookTriggerDatabase(string baseConnectionString, string schema, PostgresWebhookTriggerStore store)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        Store = store;
    }

    public PostgresWebhookTriggerStore Store { get; }

    public static async Task<WebhookTriggerDatabase> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");
        }

        var schema = $"webhook_triggers_test_{Guid.NewGuid():N}";
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
        var store = new PostgresWebhookTriggerStore(configuration);

        try
        {
            await store.InitializeAsync();
            return new WebhookTriggerDatabase(baseConnectionString, schema, store);
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
