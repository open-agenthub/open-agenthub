using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The PAT selection is persisted so a resume builds the same credential store the session was
/// created with. Against a real database because the store maps columns by ordinal: a column added
/// anywhere but the end of the SELECT list compiles and silently returns the wrong field.
/// </summary>
public class SessionGitPatIdsPostgresTests
{
    [PostgreSqlFact]
    public async Task GitPatIds_SurviveARoundTrip_IncludingTheEmptyListAndNull()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();

        await database.UpsertSessionAsync(Record("s-1", "[\"a\",\"b\"]"));
        var stored = await database.GetSessionAsync("alice", "s-1");

        Assert.NotNull(stored);
        Assert.Equal("[\"a\",\"b\"]", stored.GitPatIdsJson);
        // The columns before it still map to themselves.
        Assert.Equal("acct0001", stored.CredentialId);
        Assert.Equal("You review, you do not commit.", stored.SystemPrompt);
        Assert.Equal("what this agent is for", stored.Description);
        Assert.Equal("alice", stored.Owner);

        // An empty list ("no PAT") is distinct from null ("every PAT") and must survive as such.
        await database.UpsertSessionAsync(Record("s-1", "[]"));
        Assert.Equal("[]", (await database.GetSessionAsync("alice", "s-1"))!.GitPatIdsJson);

        await database.UpsertSessionAsync(Record("s-1", null));
        Assert.Null((await database.GetSessionAsync("alice", "s-1"))!.GitPatIdsJson);
    }

    [PostgreSqlFact]
    public async Task GitPatIds_AreNullForARowWrittenBeforeTheColumnExisted()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.ExecuteAsync(
            """
            INSERT INTO sessions (id, owner, title, mode, agent_session_id, callback_token)
            VALUES ('s-legacy', 'alice', 'Legacy', 'Interactive', 'thread-legacy', 'callback-legacy')
            """);

        Assert.Null((await database.GetSessionAsync("alice", "s-legacy"))!.GitPatIdsJson);
    }

    private static SessionRecord Record(string id, string? gitPatIdsJson) => new()
    {
        Id = id,
        Owner = "alice",
        Title = "Build triage",
        Description = "what this agent is for",
        Mode = SessionMode.Interactive,
        SystemPrompt = "You review, you do not commit.",
        CredentialId = "acct0001",
        GitPatIdsJson = gitPatIdsJson,
        AgentSessionId = $"agent-{id}",
        CallbackToken = $"callback-{id}"
    };
}
