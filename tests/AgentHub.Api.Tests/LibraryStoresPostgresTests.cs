using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibraryStoresPostgresTests
{
    [PostgreSqlFact]
    public async Task McpServerStore_CrudIsOwnerScoped()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var created = await db.McpServers.CreateAsync(
            "alice", new SaveMcpServerRequest("docs", "Documentation search", "{\"type\":\"http\",\"url\":\"https://docs.example.test\"}"));

        Assert.Single(await db.McpServers.ListByOwnerAsync("alice"));
        Assert.Empty(await db.McpServers.ListByOwnerAsync("bob"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            db.McpServers.UpdateAsync("bob", created.Id,
                new SaveMcpServerRequest("docs", null, "{\"type\":\"http\"}")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            db.McpServers.DeleteAsync("bob", created.Id));

        var updated = await db.McpServers.UpdateAsync("alice", created.Id,
            new SaveMcpServerRequest("docs2", "x", "{\"command\":\"npx\"}"));
        Assert.Equal("docs2", updated.Name);

        await db.McpServers.DeleteAsync("alice", created.Id);
        Assert.Empty(await db.McpServers.ListByOwnerAsync("alice"));
    }

    [PostgreSqlFact]
    public async Task SkillStore_FallsBackToPostgres_WithoutObjectStorage()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var created = await db.Skills.CreateAsync(
            "alice", new SaveSkillRequest("review", "Review helper", "# review skill"));

        Assert.False(created.ContentInS3);
        Assert.Equal("# review skill", await db.Skills.GetContentAsync(created));
    }

    [PostgreSqlFact]
    public async Task SkillStore_UsesObjectStorage_WhenAvailable()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync(s3Enabled: true);
        var created = await db.Skills.CreateAsync(
            "alice", new SaveSkillRequest("review", null, "# review skill"));

        Assert.True(created.ContentInS3);
        Assert.Equal("# review skill", db.Artifacts.Objects[IArtifactStore.SkillKey(created.Id)]);
        // The fallback column stays empty when the content lives in S3.
        Assert.Null(await db.ScalarAsync<string?>(
            "SELECT content FROM skills WHERE id = @id", new NpgsqlParameter("id", created.Id)));
        Assert.Equal("# review skill", await db.Skills.GetContentAsync(created));

        await db.Skills.DeleteAsync("alice", created.Id);
        Assert.False(db.Artifacts.Objects.ContainsKey(IArtifactStore.SkillKey(created.Id)));
    }

    [PostgreSqlFact]
    public async Task SkillStore_RejectsDuplicateNamesPerOwner()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.Skills.CreateAsync("alice", new SaveSkillRequest("review", null, "# a"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Skills.CreateAsync("alice", new SaveSkillRequest("review", null, "# b")));
        // The same name is fine for a different owner.
        await db.Skills.CreateAsync("bob", new SaveSkillRequest("review", null, "# c"));
    }

    [PostgreSqlFact]
    public async Task ShareStore_ResolvesAccess_ViaUserGroupAndAll()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.AddUserAsync("alice");

        var group = await db.Shares.CreateGroupAsync("devs");
        await db.Shares.SetGroupMembersAsync(group.Id, ["alice"]);

        await db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, "item-all", true, [], [], "admin");
        await db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, "item-user", false, ["alice"], [], "admin");
        await db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, "item-group", false, [], [group.Id], "admin");
        await db.Shares.SetSharesAsync(LibraryItemTypes.Skill, "other-kind", true, [], [], "admin");

        var ids = await db.Shares.ListAccessibleItemIdsAsync(LibraryItemTypes.Mcp, "alice");
        Assert.Equal(["item-all", "item-group", "item-user"], ids.OrderBy(i => i));

        // A user outside the group only sees the all-share.
        await db.AddUserAsync("bob");
        Assert.Equal(["item-all"],
            await db.Shares.ListAccessibleItemIdsAsync(LibraryItemTypes.Mcp, "bob"));
    }

    [PostgreSqlFact]
    public async Task ShareStore_SetShares_ReplacesStateAndValidatesSubjects()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.AddUserAsync("alice");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, "item", false, ["nobody"], [], "admin"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, "item", false, [], ["no-group"], "admin"));

        await db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, "item", true, ["alice"], [], "admin");
        var replaced = await db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, "item", false, [], [], "admin");
        Assert.False(replaced.All);
        Assert.Empty(replaced.Users);
        Assert.Empty(await db.Shares.ListAccessibleItemIdsAsync(LibraryItemTypes.Mcp, "alice"));
    }

    [PostgreSqlFact]
    public async Task ShareStore_DeleteGroup_CascadesMembershipAndShares()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.AddUserAsync("alice");
        var group = await db.Shares.CreateGroupAsync("devs");
        await db.Shares.SetGroupMembersAsync(group.Id, ["alice"]);
        await db.Shares.SetSharesAsync(LibraryItemTypes.Skill, "item", false, [], [group.Id], "admin");

        await db.Shares.DeleteGroupAsync(group.Id);
        Assert.Empty(await db.Shares.ListGroupsAsync());
        Assert.Empty(await db.Shares.ListAccessibleItemIdsAsync(LibraryItemTypes.Skill, "alice"));
    }

    [PostgreSqlFact]
    public async Task ShareStore_GroupNamesAreUnique_MembersMustExist()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var group = await db.Shares.CreateGroupAsync("devs");
        await Assert.ThrowsAsync<ArgumentException>(() => db.Shares.CreateGroupAsync("devs"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Shares.SetGroupMembersAsync(group.Id, ["ghost"]));
    }

    [PostgreSqlFact]
    public async Task ShareStore_SettingsToggleRoundTrips()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        Assert.False(await db.Shares.GetUserSkillPublishingAsync());
        await db.Shares.SetUserSkillPublishingAsync(true);
        Assert.True(await db.Shares.GetUserSkillPublishingAsync());
        await db.Shares.SetUserSkillPublishingAsync(false);
        Assert.False(await db.Shares.GetUserSkillPublishingAsync());
    }

    [PostgreSqlFact]
    public async Task SessionStore_RoundTripsMcpServerIds()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.Sessions.UpsertAsync(new SessionRecord
        {
            Id = "s1", Owner = "alice", Title = "t", Mode = AgentHub.Api.Models.SessionMode.Interactive,
            AgentSessionId = "as1", CallbackToken = "cb1",
            McpServerIdsJson = "[\"a\",\"b\"]"
        });
        var loaded = await db.Sessions.GetAsync("alice", "s1");
        Assert.Equal("[\"a\",\"b\"]", loaded!.McpServerIdsJson);
    }
}

internal sealed class FakeArtifactStore(bool enabled) : IArtifactStore
{
    public Dictionary<string, string> Objects { get; } = new();

    public string PresignPut(string key, TimeSpan ttl) => "";
    public string PresignGet(string key, TimeSpan ttl) => "";

    public Task<string?> GetTextAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Objects.GetValueOrDefault(key));

    public Task<bool> TryPutTextAsync(string key, string text, CancellationToken ct = default)
    {
        if (!enabled) return Task.FromResult(false);
        Objects[key] = text;
        return Task.FromResult(true);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Objects.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class PostgresLibraryDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;
    private readonly string _connectionString;
    private readonly UserDirectory _users;

    public McpServerStore McpServers { get; }
    public SkillStore Skills { get; }
    public LibraryShareStore Shares { get; }
    public PostgresSessionStore Sessions { get; }
    public FakeArtifactStore Artifacts { get; }

    private PostgresLibraryDatabase(
        string baseConnectionString, string schema, string connectionString,
        McpServerStore mcpServers, SkillStore skills, LibraryShareStore shares,
        PostgresSessionStore sessions, UserDirectory users, FakeArtifactStore artifacts)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        _connectionString = connectionString;
        McpServers = mcpServers;
        Skills = skills;
        Shares = shares;
        Sessions = sessions;
        _users = users;
        Artifacts = artifacts;
    }

    public static async Task<PostgresLibraryDatabase> CreateAsync(bool s3Enabled = false)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");
        }

        var schema = $"library_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        // Every store owns its own NpgsqlDataSource (and pool). With many parallel
        // test classes the pooled connections would exhaust Postgres' client limit,
        // so the throwaway test databases run unpooled.
        var connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schema,
            Pooling = false
        }.ConnectionString;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = connectionString
            })
            .Build();

        var artifacts = new FakeArtifactStore(s3Enabled);
        var mcpServers = new McpServerStore(configuration);
        var skills = new SkillStore(configuration, artifacts, NullLogger<SkillStore>.Instance);
        var shares = new LibraryShareStore(configuration);
        var sessions = new PostgresSessionStore(configuration);
        var users = new UserDirectory(configuration);

        try
        {
            await mcpServers.InitializeAsync();
            await skills.InitializeAsync();
            await shares.InitializeAsync();
            await sessions.InitializeAsync();
            await users.InitializeAsync();
            return new PostgresLibraryDatabase(
                baseConnectionString, schema, connectionString,
                mcpServers, skills, shares, sessions, users, artifacts);
        }
        catch
        {
            await DropSchemaAsync(baseConnectionString, schema);
            throw;
        }
    }

    public Task AddUserAsync(string owner)
        => _users.RecordLoginAsync(owner, $"{owner}@example.test", owner);

    public async Task<T?> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        var result = await command.ExecuteScalarAsync();
        if (result is null || result is DBNull)
            return default;
        return (T)result;
    }

    public ValueTask DisposeAsync()
        => new(DropSchemaAsync(_baseConnectionString, _schema));

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }
}
