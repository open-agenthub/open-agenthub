using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// <c>converted_from</c> is read by ordinal like every other column, so it is exercised against
/// a real database: a column in the wrong place in the SELECT list compiles and silently returns
/// the wrong field for every session.
/// </summary>
public class SessionConversionPostgresTests
{
    [PostgreSqlFact]
    public async Task ConvertedFrom_SurvivesARoundTrip()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();

        await database.UpsertSessionAsync(Record("s-1", SessionMode.Autonomous));
        var stored = await database.GetSessionAsync("alice", "s-1");

        Assert.NotNull(stored);
        Assert.Equal(SessionMode.Autonomous, stored.ConvertedFrom);
        Assert.Equal(SessionMode.Interactive, stored.Mode);
        // The column before it still maps to itself.
        Assert.Equal("acct-1", stored.CredentialId);
        Assert.Equal("triage the failing build", stored.Prompt);

        // A record that was never converted stays null through an upsert.
        await database.UpsertSessionAsync(Record("s-2", null));
        Assert.Null((await database.GetSessionAsync("alice", "s-2"))!.ConvertedFrom);
    }

    [PostgreSqlFact]
    public async Task ConvertedFrom_IsNullForARowWrittenBeforeTheColumnExisted()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("alice", "s-legacy");

        Assert.Null((await database.GetSessionAsync("alice", "s-legacy"))!.ConvertedFrom);
    }

    private static SessionRecord Record(string id, SessionMode? convertedFrom) => new()
    {
        Id = id,
        Owner = "alice",
        Title = "Build triage",
        Mode = SessionMode.Interactive,
        Prompt = "triage the failing build",
        CredentialId = "acct-1",
        ConvertedFrom = convertedFrom,
        AgentSessionId = $"agent-{id}",
        CallbackToken = $"callback-{id}"
    };
}
