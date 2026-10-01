using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The system prompt is persisted so a resumed pod is given the instructions the session was
/// created with. Exercised against a real database because the store reads its columns by ordinal:
/// a new column in the wrong place in the SELECT list does not fail to compile, it silently returns
/// the wrong field for every session.
/// </summary>
public class SessionSystemPromptPostgresTests
{
    [PostgreSqlFact]
    public async Task SystemPrompt_SurvivesARoundTripAndCanBeCleared()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();

        await database.UpsertSessionAsync(Record("s-1", "You review, you do not commit."));
        var stored = await database.GetSessionAsync("alice", "s-1");

        Assert.NotNull(stored);
        Assert.Equal("You review, you do not commit.", stored.SystemPrompt);
        // Everything after the new column still maps to itself.
        Assert.Equal("triage the failing build", stored.Prompt);
        Assert.Equal("what this agent is for", stored.Description);
        Assert.Equal(SessionUiMode.Terminal, stored.UiMode);
        Assert.Equal("alice", stored.Owner);

        await database.UpsertSessionAsync(Record("s-1", null));
        Assert.Null((await database.GetSessionAsync("alice", "s-1"))!.SystemPrompt);
    }

    [PostgreSqlFact]
    public async Task SystemPrompt_IsNullForARowWrittenBeforeTheColumnExisted()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("alice", "s-legacy");

        Assert.Null((await database.GetSessionAsync("alice", "s-legacy"))!.SystemPrompt);
    }

    private static SessionRecord Record(string id, string? systemPrompt) => new()
    {
        Id = id,
        Owner = "alice",
        Title = "Build triage",
        Description = "what this agent is for",
        Mode = SessionMode.Interactive,
        Prompt = "triage the failing build",
        SystemPrompt = systemPrompt,
        AgentSessionId = $"agent-{id}",
        CallbackToken = $"callback-{id}"
    };
}
