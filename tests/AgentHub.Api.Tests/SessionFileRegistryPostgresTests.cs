using AgentHub.Api.Files;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionFileRegistryPostgresTests
{
    [PostgreSqlFact]
    public async Task Insert_get_and_list_are_scoped_to_the_session()
    {
        await using var db = await SessionFilePostgresFixture.CreateAsync();
        await db.Registry.InsertAsync(File("f1", "s1"));
        await db.Registry.InsertAsync(File("f2", "s2"));

        Assert.NotNull(await db.Registry.GetAsync("s1", "f1"));
        Assert.Null(await db.Registry.GetAsync("s2", "f1"));
        var files = await db.Registry.ListAsync("s1");
        Assert.Single(files);
        Assert.Equal("f1", files[0].Id);
    }

    [PostgreSqlFact]
    public async Task Presentation_revision_increases_and_never_returns_a_stale_selection()
    {
        await using var db = await SessionFilePostgresFixture.CreateAsync();
        // Only a Ready file can be presented — a Reserved one is rejected by design.
        await db.Registry.InsertAsync(File("f1", "s1", SessionFileState.Ready));

        var first = await db.Registry.SetPresentationAsync("s1", "f1", "alice");
        var second = await db.Registry.SetPresentationAsync("s1", null, "alice");

        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        Assert.Null((await db.Registry.GetPresentationAsync("s1"))!.FileId);
    }

    [PostgreSqlFact]
    public async Task Ready_usage_excludes_failed_expired_and_deleted_rows()
    {
        await using var db = await SessionFilePostgresFixture.CreateAsync();
        await db.SeedAsync(
            File("ready", "s1", SessionFileState.Ready, 11),
            File("failed", "s1", SessionFileState.Failed, 13),
            File("expired", "s1", SessionFileState.Expired, 17),
            File("gone", "s1", SessionFileState.Deleted, 19));

        Assert.Equal(new SessionFileUsage(1, 11), await db.Registry.GetUsageAsync("s1"));
    }

    [PostgreSqlFact]
    public async Task Transition_only_changes_the_expected_state()
    {
        await using var db = await SessionFilePostgresFixture.CreateAsync();
        await db.Registry.InsertAsync(File("f1", "s1", SessionFileState.Reserved));

        Assert.False(await db.Registry.TransitionAsync(
            "s1", "f1", SessionFileState.Uploading, SessionFileState.Ready,
            "image/png", 10));
        Assert.True(await db.Registry.TransitionAsync(
            "s1", "f1", SessionFileState.Reserved, SessionFileState.Ready,
            "image/png", 10));
        Assert.Equal(SessionFileState.Ready, (await db.Registry.GetAsync("s1", "f1"))!.State);
    }

    [PostgreSqlFact]
    public async Task Preview_claim_and_link_update_the_source_and_derivative()
    {
        await using var db = await SessionFilePostgresFixture.CreateAsync();
        await db.Registry.InsertAsync(File(
            "source", "s1", SessionFileState.Ready, 10, SessionFilePreviewState.Queued));
        await db.Registry.InsertAsync(File("preview", "s1", SessionFileState.Ready, 5));

        var claimed = await db.Registry.ClaimPreviewAsync();
        Assert.Equal("source", claimed!.Id);
        Assert.Equal(SessionFilePreviewState.Converting,
            (await db.Registry.GetAsync("s1", "source"))!.PreviewState);

        await db.Registry.LinkPreviewAsync("source", "preview", succeeded: true);
        var linked = await db.Registry.GetAsync("s1", "source");
        Assert.Equal(SessionFilePreviewState.Ready, linked!.PreviewState);
        Assert.Equal("preview", linked.PreviewFileId);
    }

    [PostgreSqlFact]
    public async Task Expiry_and_session_deletion_return_or_hide_the_affected_rows()
    {
        await using var db = await SessionFilePostgresFixture.CreateAsync();
        await db.Registry.InsertAsync(File("old", "s1", createdAt: DateTime.UtcNow.AddHours(-1)));
        await db.Registry.InsertAsync(File("fresh", "s1", createdAt: DateTime.UtcNow));

        var expired = await db.Registry.ExpireReservationsAsync(DateTime.UtcNow.AddMinutes(-30));
        Assert.Single(expired);
        Assert.Equal("old", expired[0].Id);

        await db.Registry.MarkSessionDeletedAsync("s1");
        Assert.Empty(await db.Registry.ListAsync("s1"));
    }

    private static SessionFileRecord File(
        string id,
        string sessionId,
        SessionFileState state = SessionFileState.Reserved,
        long size = 10,
        SessionFilePreviewState previewState = SessionFilePreviewState.None,
        DateTime? createdAt = null) => new(
            id, sessionId, "alice", $"{id}.png", ".png", "image/png",
            state == SessionFileState.Ready ? "image/png" : null, size,
            SessionFileStorageKind.Pod, $"{id}/{id}.png", state, previewState,
            null, "alice", "user", createdAt ?? DateTime.UtcNow,
            state == SessionFileState.Ready ? DateTime.UtcNow : null,
            DateTime.UtcNow.AddHours(1));
}

internal sealed class SessionFilePostgresFixture : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;

    private SessionFilePostgresFixture(
        string baseConnectionString,
        string schema,
        string schemaConnectionString,
        PostgresSessionFileRegistry registry)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        ConnectionString = schemaConnectionString;
        Registry = registry;
    }

    public PostgresSessionFileRegistry Registry { get; }

    /// <summary>Scoped to this fixture's schema, for collaborators that open their own connection.</summary>
    public string ConnectionString { get; }

    public static async Task<SessionFilePostgresFixture> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");
        }

        var schema = $"session_files_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schema,
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = builder.ConnectionString,
            })
            .Build();
        var registry = new PostgresSessionFileRegistry(configuration);

        try
        {
            await registry.InitializeAsync();
            return new SessionFilePostgresFixture(
                baseConnectionString, schema, builder.ConnectionString, registry);
        }
        catch
        {
            await DropSchemaAsync(baseConnectionString, schema);
            throw;
        }
    }

    public async Task SeedAsync(params SessionFileRecord[] files)
    {
        foreach (var file in files)
        {
            await Registry.InsertAsync(file);
        }
    }

    public ValueTask DisposeAsync() =>
        new(DropSchemaAsync(_baseConnectionString, _schema));

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }
}
