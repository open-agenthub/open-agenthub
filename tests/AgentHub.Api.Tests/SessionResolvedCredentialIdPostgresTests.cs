using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The account a start mounted is persisted so a limit report from an unpinned session names the
/// right login (docs/account-limits.md). Against a real database because the store maps columns
/// by ordinal: a column added anywhere but the end of the SELECT list compiles and silently
/// returns the wrong field.
/// </summary>
public class SessionResolvedCredentialIdPostgresTests
{
    [PostgreSqlFact]
    public async Task ResolvedCredentialId_SurvivesARoundTrip_AndTheColumnUpdate()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();

        await database.UpsertSessionAsync(Record("s-1", "acct0002"));
        var stored = await database.GetSessionAsync("alice", "s-1");

        Assert.NotNull(stored);
        Assert.Equal("acct0002", stored.ResolvedCredentialId);
        // The columns before it still map to themselves.
        Assert.Equal("[\"a\"]", stored.GitPatIdsJson);
        Assert.Equal("acct0001", stored.CredentialId);
        Assert.Equal("what this agent is for", stored.Description);
        Assert.Equal("alice", stored.Owner);

        // The spawn writes the column alone, without touching the rest of the row.
        await database.Sessions.SetResolvedCredentialIdAsync("s-1", "acct0003");
        var after = await database.GetSessionAsync("alice", "s-1");
        Assert.Equal("acct0003", after!.ResolvedCredentialId);
        Assert.Equal("acct0001", after.CredentialId);
        Assert.Equal(stored.UpdatedAt, after.UpdatedAt);

        await database.Sessions.SetResolvedCredentialIdAsync("s-1", null);
        Assert.Null((await database.GetSessionAsync("alice", "s-1"))!.ResolvedCredentialId);
    }

    [PostgreSqlFact]
    public async Task ResolvedCredentialId_IsNullForARowWrittenBeforeTheColumnExisted()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.ExecuteAsync(
            """
            INSERT INTO sessions (id, owner, title, mode, agent_session_id, callback_token)
            VALUES ('s-legacy', 'alice', 'Legacy', 'Interactive', 'thread-legacy', 'callback-legacy')
            """);

        Assert.Null((await database.GetSessionAsync("alice", "s-legacy"))!.ResolvedCredentialId);
    }

    private static SessionRecord Record(string id, string? resolvedCredentialId) => new()
    {
        Id = id,
        Owner = "alice",
        Title = "Build triage",
        Description = "what this agent is for",
        Mode = SessionMode.Interactive,
        CredentialId = "acct0001",
        GitPatIdsJson = "[\"a\"]",
        ResolvedCredentialId = resolvedCredentialId,
        AgentSessionId = $"agent-{id}",
        CallbackToken = $"callback-{id}"
    };
}
