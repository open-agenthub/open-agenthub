using AgentHub.Api.Ee.Identity;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibraryStoresPostgresTests
{
    private static SaveMcpServerRequest RawRequest(
        string name,
        string? description = null,
        string configJson = "{\"type\":\"http\",\"url\":\"https://docs.example.test\"}",
        string? secretJson = null) =>
        new(name, description, "raw", configJson, secretJson);

    private static SaveMcpServerRequest ApiRequest(
        string name,
        string? description = null,
        string configJson = "{\"specType\":\"openapi\",\"specUrl\":\"https://api.example.test/openapi.json\"}",
        string? secretJson = null) =>
        new(name, description, "api", configJson, secretJson);

    [Fact]
    public async Task InMemory_Create_ListByOwner_IsOwnerScoped()
    {
        var store = new InMemoryMcpServerStore();
        await store.CreateAsync("alice", RawRequest("docs", "Documentation search"));
        await store.CreateAsync("bob", RawRequest("other"));

        var alice = await store.ListByOwnerAsync("alice");
        Assert.Single(alice);
        Assert.Equal("docs", alice[0].Name);
        Assert.Equal("raw", alice[0].Kind);
        Assert.Empty(await store.ListByOwnerAsync("carol"));
    }

    [Fact]
    public async Task InMemory_GetMany_ReturnsMatchingIds_RegardlessOfOwner()
    {
        var store = new InMemoryMcpServerStore();
        var a = await store.CreateAsync("alice", RawRequest("alpha"));
        var b = await store.CreateAsync("bob", ApiRequest("beta", secretJson: "{\"token\":\"t\"}"));
        await store.CreateAsync("alice", RawRequest("gamma"));

        var many = await store.GetManyAsync([b.Id, a.Id, "missing"]);
        Assert.Equal(2, many.Count);
        Assert.Equal(["alpha", "beta"], many.Select(r => r.Name).ToArray());
        Assert.Equal("api", many.Single(r => r.Id == b.Id).Kind);
        Assert.Equal("{\"token\":\"t\"}", many.Single(r => r.Id == b.Id).SecretJson);
    }

    [Fact]
    public async Task InMemory_Update_IsOwnerScoped_AndPersistsKindSecret()
    {
        var store = new InMemoryMcpServerStore();
        var created = await store.CreateAsync("alice", RawRequest("docs"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.UpdateAsync("bob", created.Id, RawRequest("stolen")));

        var updated = await store.UpdateAsync(
            "alice", created.Id,
            ApiRequest("docs2", "x", secretJson: "{\"token\":\"secret\"}"));
        Assert.Equal("docs2", updated.Name);
        Assert.Equal("api", updated.Kind);
        Assert.Equal("{\"token\":\"secret\"}", updated.SecretJson);
        Assert.Contains("specUrl", updated.ConfigJson);
    }

    [Fact]
    public async Task InMemory_Delete_IsOwnerScoped()
    {
        var store = new InMemoryMcpServerStore();
        var created = await store.CreateAsync("alice", RawRequest("docs"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.DeleteAsync("bob", created.Id));
        Assert.Single(await store.ListByOwnerAsync("alice"));

        await store.DeleteAsync("alice", created.Id);
        Assert.Empty(await store.ListByOwnerAsync("alice"));
    }

    [Fact]
    public async Task InMemory_Create_RejectsDuplicateNamePerOwner_CaseInsensitive()
    {
        var store = new InMemoryMcpServerStore();
        await store.CreateAsync("alice", RawRequest("Docs"));
        await store.CreateAsync("bob", RawRequest("docs")); // other owner OK

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CreateAsync("alice", RawRequest("docs")));
        Assert.Contains("already exists", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InMemory_Update_RejectsDuplicateNamePerOwner()
    {
        var store = new InMemoryMcpServerStore();
        await store.CreateAsync("alice", RawRequest("alpha"));
        var beta = await store.CreateAsync("alice", RawRequest("beta"));

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpdateAsync("alice", beta.Id, RawRequest("ALPHA")));
        Assert.Contains("already exists", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [PostgreSqlFact]
    public async Task McpServerStore_CrudIsOwnerScoped()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var created = await db.McpServers.CreateAsync(
            "alice", RawRequest("docs", "Documentation search"));

        Assert.Single(await db.McpServers.ListByOwnerAsync("alice"));
        Assert.Empty(await db.McpServers.ListByOwnerAsync("bob"));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            db.McpServers.UpdateAsync("bob", created.Id, RawRequest("docs")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            db.McpServers.DeleteAsync("bob", created.Id));

        var updated = await db.McpServers.UpdateAsync(
            "alice", created.Id, RawRequest("docs2", "x", "{\"command\":\"npx\"}"));
        Assert.Equal("docs2", updated.Name);

        await db.McpServers.DeleteAsync("alice", created.Id);
        Assert.Empty(await db.McpServers.ListByOwnerAsync("alice"));
    }

    [PostgreSqlFact]
    public async Task McpServerStore_PersistsKindAndSecretJson()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var created = await db.McpServers.CreateAsync(
            "alice",
            ApiRequest("gateway", "API wrap", secretJson: "{\"token\":\"abc\"}"));

        Assert.Equal("api", created.Kind);
        Assert.Equal("{\"token\":\"abc\"}", created.SecretJson);

        var listed = Assert.Single(await db.McpServers.ListByOwnerAsync("alice"));
        Assert.Equal("api", listed.Kind);
        Assert.Equal("{\"token\":\"abc\"}", listed.SecretJson);

        var kind = await db.ScalarAsync<string>(
            "SELECT kind FROM mcp_servers WHERE id = @id", new NpgsqlParameter("id", created.Id));
        var secret = await db.ScalarAsync<string?>(
            "SELECT secret_json FROM mcp_servers WHERE id = @id", new NpgsqlParameter("id", created.Id));
        Assert.Equal("api", kind);
        Assert.Equal("{\"token\":\"abc\"}", secret);

        var preserved = await db.McpServers.UpdateAsync(
            "alice", created.Id, ApiRequest("gateway", secretJson: null));
        Assert.Equal("{\"token\":\"abc\"}", preserved.SecretJson);

        var cleared = await db.McpServers.UpdateAsync(
            "alice", created.Id, ApiRequest("gateway", secretJson: ""));
        Assert.Null(cleared.SecretJson);
    }

    [PostgreSqlFact]
    public async Task McpServerStore_GetMany_CrossOwner()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var a = await db.McpServers.CreateAsync("alice", RawRequest("alpha"));
        var b = await db.McpServers.CreateAsync(
            McpServerRecord.OrgOwner, ApiRequest("org-api"));
        await db.McpServers.CreateAsync("alice", RawRequest("gamma"));

        var many = await db.McpServers.GetManyAsync([b.Id, a.Id, "missing"]);
        Assert.Equal(["alpha", "org-api"], many.Select(r => r.Name).ToArray());
    }

    [PostgreSqlFact]
    public async Task McpServerStore_OrgOwner_IsQueryable()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.McpServers.CreateAsync(McpServerRecord.OrgOwner, RawRequest("shared"));
        await db.McpServers.CreateAsync("alice", RawRequest("personal"));

        Assert.Single(await db.McpServers.ListByOwnerAsync(McpServerRecord.OrgOwner));
        Assert.Single(await db.McpServers.ListByOwnerAsync("alice"));
    }

    [PostgreSqlFact]
    public async Task McpServerStore_Create_RejectsDuplicateNamePerOwner_CaseInsensitive()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.McpServers.CreateAsync("alice", RawRequest("Docs"));
        await db.McpServers.CreateAsync("bob", RawRequest("docs")); // other owner OK

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            db.McpServers.CreateAsync("alice", RawRequest("docs")));
        Assert.Contains("already exists", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [PostgreSqlFact]
    public async Task McpServerStore_Update_RejectsDuplicateNamePerOwner()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.McpServers.CreateAsync("alice", RawRequest("alpha"));
        var beta = await db.McpServers.CreateAsync("alice", RawRequest("beta"));

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            db.McpServers.UpdateAsync("alice", beta.Id, RawRequest("ALPHA")));
        Assert.Contains("already exists", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [PostgreSqlFact]
    public async Task LibraryShareStore_SetGet_AndAccessibleViaAllUserGroup()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.EnsureUserAsync("alice");
        await db.EnsureUserAsync("bob");
        await db.Groups.ReplaceGroupsAsync("alice", ["devs"]);

        var viaAll = await db.McpServers.CreateAsync("bob", RawRequest("via-all"));
        var viaUser = await db.McpServers.CreateAsync("bob", RawRequest("via-user"));
        var viaGroup = await db.McpServers.CreateAsync("bob", RawRequest("via-group"));
        var hidden = await db.McpServers.CreateAsync("bob", RawRequest("hidden"));

        await db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, viaAll.Id, all: true, null, null, "bob");
        await db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, viaUser.Id, all: false, ["alice"], null, "bob");
        await db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, viaGroup.Id, all: false, null, ["devs"], "bob");

        var allState = await db.Shares.GetSharesAsync(LibraryItemTypes.Mcp, viaAll.Id);
        Assert.True(allState.All);
        Assert.Empty(allState.Users);
        Assert.Empty(allState.Groups);

        var userState = await db.Shares.GetSharesAsync(LibraryItemTypes.Mcp, viaUser.Id);
        Assert.False(userState.All);
        Assert.Equal(["alice"], userState.Users);

        var groupState = await db.Shares.GetSharesAsync(LibraryItemTypes.Mcp, viaGroup.Id);
        Assert.Equal(["devs"], groupState.Groups);

        var accessible = (await db.Shares.ListAccessibleItemIdsAsync(LibraryItemTypes.Mcp, "alice"))
            .OrderBy(id => id).ToList();
        Assert.Equal(
            new[] { viaAll.Id, viaUser.Id, viaGroup.Id }.OrderBy(id => id),
            accessible);
        Assert.DoesNotContain(hidden.Id, accessible);
    }

    [PostgreSqlFact]
    public async Task LibraryShareStore_RejectsUnknownUserAndGroup()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        var server = await db.McpServers.CreateAsync("bob", RawRequest("docs"));

        var unknownUser = await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, server.Id, false, ["ghost"], null, "bob"));
        Assert.Contains("ghost", unknownUser.Message);

        var unknownGroup = await Assert.ThrowsAsync<ArgumentException>(() =>
            db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, server.Id, false, null, ["nope"], "bob"));
        Assert.Contains("nope", unknownGroup.Message);
    }

    [PostgreSqlFact]
    public async Task LibraryShareStore_DeleteForItem_ClearsShares()
    {
        await using var db = await PostgresLibraryDatabase.CreateAsync();
        await db.EnsureUserAsync("alice");
        var server = await db.McpServers.CreateAsync("bob", RawRequest("docs"));
        await db.Shares.SetSharesAsync(LibraryItemTypes.Mcp, server.Id, all: true, null, null, "bob");
        Assert.Contains(server.Id, await db.Shares.ListAccessibleItemIdsAsync(LibraryItemTypes.Mcp, "alice"));

        await db.Shares.DeleteForItemAsync(LibraryItemTypes.Mcp, server.Id);
        Assert.Empty(await db.Shares.ListAccessibleItemIdsAsync(LibraryItemTypes.Mcp, "alice"));
        var state = await db.Shares.GetSharesAsync(LibraryItemTypes.Mcp, server.Id);
        Assert.False(state.All);
        Assert.Empty(state.Users);
        Assert.Empty(state.Groups);
    }
}

internal sealed class PostgresLibraryDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;
    private readonly string _connectionString;

    public McpServerStore McpServers { get; }
    public LibraryShareStore Shares { get; }
    public UserGroupStore Groups { get; }
    public UserDirectory Users { get; }

    private PostgresLibraryDatabase(
        string baseConnectionString,
        string schema,
        string connectionString,
        McpServerStore mcpServers,
        LibraryShareStore shares,
        UserGroupStore groups,
        UserDirectory users)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        _connectionString = connectionString;
        McpServers = mcpServers;
        Shares = shares;
        Groups = groups;
        Users = users;
    }

    public static async Task<PostgresLibraryDatabase> CreateAsync()
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

        var mcpServers = new McpServerStore(configuration);
        var shares = new LibraryShareStore(configuration);
        var groups = new UserGroupStore(configuration);
        var users = new UserDirectory(configuration);
        try
        {
            await mcpServers.InitializeAsync();
            await shares.InitializeAsync();
            await groups.InitializeAsync();
            await users.InitializeAsync();
            return new PostgresLibraryDatabase(
                baseConnectionString, schema, connectionString, mcpServers, shares, groups, users);
        }
        catch
        {
            await DropSchemaAsync(baseConnectionString, schema);
            throw;
        }
    }

    public Task EnsureUserAsync(string owner)
        => Users.RecordLoginAsync(owner, $"{owner}@example.test", owner);

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
