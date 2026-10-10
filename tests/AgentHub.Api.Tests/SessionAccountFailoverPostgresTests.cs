using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The per-session failover setting is persisted (docs/account-limits.md); null reads as auto.
/// Against a real database because the store maps columns by ordinal: a column added anywhere
/// but the end of the SELECT list compiles and silently returns the wrong field.
/// </summary>
public class SessionAccountFailoverPostgresTests
{
    [PostgreSqlFact]
    public async Task AccountFailover_SurvivesARoundTrip_AndNullIsAuto()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();

        await database.UpsertSessionAsync(Record("s-1", "off"));
        var stored = await database.GetSessionAsync("alice", "s-1");

        Assert.NotNull(stored);
        Assert.Equal("off", stored.AccountFailover);
        // The columns before it still map to themselves.
        Assert.Equal("acct0002", stored.ResolvedCredentialId);
        Assert.Equal("[\"a\"]", stored.GitPatIdsJson);
        Assert.Equal("acct0001", stored.CredentialId);
        Assert.Equal("alice", stored.Owner);

        await database.UpsertSessionAsync(Record("s-1", null));
        Assert.Null((await database.GetSessionAsync("alice", "s-1"))!.AccountFailover);
    }

    [PostgreSqlFact]
    public async Task AccountFailover_IsNullForARowWrittenBeforeTheColumnExisted()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.ExecuteAsync(
            """
            INSERT INTO sessions (id, owner, title, mode, agent_session_id, callback_token)
            VALUES ('s-legacy', 'alice', 'Legacy', 'Interactive', 'thread-legacy', 'callback-legacy')
            """);

        Assert.Null((await database.GetSessionAsync("alice", "s-legacy"))!.AccountFailover);
    }

    private static SessionRecord Record(string id, string? accountFailover) => new()
    {
        Id = id,
        Owner = "alice",
        Title = "Build triage",
        Mode = SessionMode.Interactive,
        CredentialId = "acct0001",
        ResolvedCredentialId = "acct0002",
        GitPatIdsJson = "[\"a\"]",
        AccountFailover = accountFailover,
        AgentSessionId = $"agent-{id}",
        CallbackToken = $"callback-{id}"
    };
}
