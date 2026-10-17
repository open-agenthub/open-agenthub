using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The secret selections of sessions and projects (docs/session-secrets.md), against a real
/// database: both stores map columns by ordinal, so a column in the wrong place compiles and
/// silently returns the wrong field.
/// </summary>
public class SessionSecretIdsPostgresTests
{
    [PostgreSqlFact]
    public async Task SessionSecretIds_SurviveARoundTrip_AndLeaveTheOtherColumnsInPlace()
    {
        await using var database = await PostgresPurgeDatabase.CreateAsync();

        await database.Sessions.UpsertAsync(Record("s-1", "[\"a\"]"));
        var stored = await database.Sessions.GetAsync("alice", "s-1");
        Assert.Equal("[\"a\"]", stored!.SecretIdsJson);
        Assert.Equal("[\"pat\"]", stored.GitPatIdsJson);
        Assert.Equal("off", stored.AccountFailover);

        // "None" and "follow the project" are different selections and must stay apart.
        await database.Sessions.UpsertAsync(Record("s-1", "[]"));
        Assert.Equal("[]", (await database.Sessions.GetAsync("alice", "s-1"))!.SecretIdsJson);
        await database.Sessions.UpsertAsync(Record("s-1", null));
        Assert.Null((await database.Sessions.GetAsync("alice", "s-1"))!.SecretIdsJson);
    }

    [PostgreSqlFact]
    public async Task ProjectSecretIds_AreStoredOnCreate_ReplacedOnUpdate_AndUntouchedByARename()
    {
        await using var database = await PostgresPurgeDatabase.CreateAsync();

        var created = await database.Projects.CreateAsync("alice", new CreateProjectRequest("Ops", "#112233", ["a", "b"]));
        Assert.Equal(["a", "b"], created.SecretIds);

        var renamed = await database.Projects.UpdateAsync("alice", created.Id, new UpdateProjectRequest("Ops 2", null, null));
        Assert.Equal(["a", "b"], renamed!.SecretIds);
        Assert.Equal("#112233", renamed.Color);

        var narrowed = await database.Projects.UpdateAsync("alice", created.Id, new UpdateProjectRequest(null, null, null, ["b"]));
        Assert.Equal(["b"], narrowed!.SecretIds);
        var cleared = await database.Projects.UpdateAsync("alice", created.Id, new UpdateProjectRequest(null, null, null, []));
        Assert.Empty(cleared!.SecretIds!);

        var listed = Assert.Single(await database.Projects.ListAsync("alice"));
        Assert.Equal(("Ops 2", 0), (listed.Name, listed.SortOrder));
        Assert.Empty(listed.SecretIds!);
    }

    [PostgreSqlFact]
    public async Task AProjectWithoutASelection_SelectsNothing()
    {
        await using var database = await PostgresPurgeDatabase.CreateAsync();
        var created = await database.Projects.CreateAsync("alice", new CreateProjectRequest("Plain", null));

        Assert.Empty((await database.Projects.GetAsync("alice", created.Id))!.SecretIds!);
    }

    private static SessionRecord Record(string id, string? secretIdsJson) => new()
    {
        Id = id,
        Owner = "alice",
        Title = "Deploy",
        Mode = SessionMode.Interactive,
        GitPatIdsJson = "[\"pat\"]",
        AccountFailover = "off",
        SecretIdsJson = secretIdsJson,
        AgentSessionId = $"agent-{id}",
        CallbackToken = $"callback-{id}"
    };
}

/// <summary>Secret requests live in Postgres because the tool polls through one replica while the
/// owner answers through another; the first answer has to win on every one of them.</summary>
public class SecretRequestStorePostgresTests
{
    [PostgreSqlFact]
    public async Task ARequest_IsResolvedOnce_AndListsOnlyWhileOpen()
    {
        await using var database = await PostgresPurgeDatabase.CreateAsync();
        var store = new PostgresSecretRequestStore(database.Config);
        await store.InitializeAsync();
        await store.CreateAsync(new SecretRequest { Id = "r1", SessionId = "s1", Owner = "alice", Name = "A_TOKEN", Delivery = "env", Purpose = "deploy" });
        await store.CreateAsync(new SecretRequest { Id = "r2", SessionId = "s2", Owner = "alice", Name = "b.pem", Delivery = "file" });

        Assert.Equal(["r2", "r1"], (await store.ListPendingAsync("alice")).Select(r => r.Id).OrderByDescending(id => id));
        Assert.Equal(["r1"], (await store.ListPendingAsync("alice", "s1")).Select(r => r.Id));
        Assert.Empty(await store.ListPendingAsync("bob"));

        Assert.True(await store.ResolveAsync("r1", SecretRequest.Provided, "sec1"));
        Assert.False(await store.ResolveAsync("r1", SecretRequest.Expired, null));
        var resolved = await store.GetAsync("r1");
        Assert.Equal((SecretRequest.Provided, "sec1", "deploy"), (resolved!.Status, resolved.SecretId, resolved.Purpose));
        Assert.Equal(["r2"], (await store.ListPendingAsync("alice")).Select(r => r.Id));

        await store.DeleteBySessionAsync("s2");
        Assert.Null(await store.GetAsync("r2"));
    }

    [PostgreSqlFact]
    public async Task GrantsAndCeiling_SurviveARoundTrip_BehindTheOtherSecretColumns()
    {
        await using var database = await PostgresPurgeDatabase.CreateAsync();
        await database.Sessions.UpsertAsync(new SessionRecord
        {
            Id = "s1", Owner = "alice", Title = "t", Mode = SessionMode.Interactive, AgentSessionId = "a", CallbackToken = "c",
            SecretIdsJson = "[\"a\"]", SecretGrantIdsJson = "[\"g\"]", SecretCeilingJson = "[\"a\",\"g\"]", FailureReason = "why"
        });
        var stored = await database.Sessions.GetAsync("alice", "s1");
        Assert.Equal(("[\"a\"]", "[\"g\"]", "[\"a\",\"g\"]", "why"),
            (stored!.SecretIdsJson, stored.SecretGrantIdsJson, stored.SecretCeilingJson, stored.FailureReason));
    }
}
