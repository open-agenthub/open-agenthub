using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The three self-deletion columns against a real database — the store maps by ordinal, so a
/// column in the wrong place in the select list returns the wrong field for every session without
/// failing to compile — plus the two store operations the sweep depends on.
/// </summary>
public class SessionExpiryPostgresTests
{
    [PostgreSqlFact]
    public async Task Setting_SurvivesARoundTrip_AndEverythingBehindItStillMaps()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();

        await database.UpsertSessionAsync(Record("s-1", 7200, "lastActivity"));
        var stored = await database.GetSessionAsync("alice", "s-1");

        Assert.NotNull(stored);
        Assert.Equal(7200, stored.AutoDeleteAfterSeconds);
        Assert.Equal("lastActivity", stored.AutoDeleteFrom);
        Assert.Equal(stored.CreatedAt, stored.LastActivityAt, TimeSpan.FromSeconds(1));
        // The columns were appended after credential_id; the fields before them map to themselves.
        Assert.Equal("acct-1", stored.CredentialId);
        Assert.Equal("You review, you do not commit.", stored.SystemPrompt);
        Assert.Equal("triage the failing build", stored.Prompt);

        await database.UpsertSessionAsync(Record("s-1", null, null));
        var cleared = await database.GetSessionAsync("alice", "s-1");
        Assert.Null(cleared!.AutoDeleteAfterSeconds);
        Assert.Null(cleared.AutoDeleteFrom);
    }

    [PostgreSqlFact]
    public async Task ARowWrittenBeforeTheColumnsExisted_HasNoDeadline_AndReadsCreationAsLastActivity()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.AddSessionAsync("alice", "s-legacy");
        await database.ExecuteAsync("UPDATE sessions SET last_activity_at = NULL WHERE id = 's-legacy'");

        var stored = await database.GetSessionAsync("alice", "s-legacy");

        Assert.Null(stored!.AutoDeleteAfterSeconds);
        Assert.Null(stored.AutoDeleteFrom);
        Assert.Equal(stored.CreatedAt, stored.LastActivityAt);
    }

    [PostgreSqlFact]
    public async Task Touch_MovesLastActivity_WithoutBumpingUpdatedAt()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.UpsertSessionAsync(Record("s-1", 7200, "lastActivity"));
        var past = DateTime.UtcNow.AddHours(-3);
        await database.ExecuteAsync("UPDATE sessions SET last_activity_at = @t, updated_at = @t WHERE id = 's-1'",
            new NpgsqlParameter("t", past));

        await database.Sessions.TouchActivityAsync("s-1");
        var stored = await database.GetSessionAsync("alice", "s-1");

        Assert.True(stored!.LastActivityAt > past.AddHours(2), "last_activity_at was not touched");
        // updated_at is what the rest of the app reads as "edited"; a touch is not an edit.
        Assert.Equal(past, stored.UpdatedAt, TimeSpan.FromSeconds(1));
    }

    [PostgreSqlFact]
    public async Task Upsert_KeepsATouchThatLandedWhileTheRecordWasBeingEdited()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var record = Record("s-1", 7200, "lastActivity");
        await database.UpsertSessionAsync(record);
        var stale = (await database.GetSessionAsync("alice", "s-1"))!;
        var past = DateTime.UtcNow.AddHours(-3);
        await database.ExecuteAsync("UPDATE sessions SET last_activity_at = @t WHERE id = 's-1'", new NpgsqlParameter("t", past));

        // An edit writes the whole record back; its copy of last_activity_at is older than the row's.
        stale.Title = "renamed";
        stale.LastActivityAt = DateTime.UtcNow;
        await database.UpsertSessionAsync(stale);

        var stored = await database.GetSessionAsync("alice", "s-1");
        Assert.Equal("renamed", stored!.Title);
        Assert.Equal(past, stored.LastActivityAt, TimeSpan.FromSeconds(1));
    }

    [PostgreSqlFact]
    public async Task UpdateStatus_CountsAsActivity()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        await database.UpsertSessionAsync(Record("s-1", 7200, "lastActivity"));
        var past = DateTime.UtcNow.AddHours(-3);
        await database.ExecuteAsync("UPDATE sessions SET last_activity_at = @t WHERE id = 's-1'", new NpgsqlParameter("t", past));

        await database.Sessions.UpdateStatusAsync("s-1", "Succeeded");

        var stored = await database.GetSessionAsync("alice", "s-1");
        Assert.Equal("Succeeded", stored!.Status);
        Assert.True(stored.LastActivityAt > past.AddHours(2));
    }

    [PostgreSqlFact]
    public async Task ListExpired_FindsDueSessionsOfEveryOwner_OnTheirOwnBasis()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        var p = (string n, DateTime t) => new NpgsqlParameter(n, t);

        // Due from start: created three hours ago, one-hour deadline, touched a minute ago (ignored).
        await database.UpsertSessionAsync(Record("due-start", 3600, "start", owner: "alice"));
        await database.ExecuteAsync("UPDATE sessions SET created_at = @c, last_activity_at = @a WHERE id = 'due-start'",
            p("c", now.AddHours(-3)), p("a", now.AddMinutes(-1)));
        // Due from activity: created long ago, last touched two hours ago, one-hour deadline.
        await database.UpsertSessionAsync(Record("due-idle", 3600, "lastActivity", owner: "bob"));
        await database.ExecuteAsync("UPDATE sessions SET created_at = @c, last_activity_at = @a WHERE id = 'due-idle'",
            p("c", now.AddDays(-1)), p("a", now.AddHours(-2)));
        // A legacy row never touched: creation (four hours ago) is the basis.
        await database.UpsertSessionAsync(Record("due-legacy", 3600, "lastActivity", owner: "carol"));
        await database.ExecuteAsync("UPDATE sessions SET created_at = @c, last_activity_at = NULL WHERE id = 'due-legacy'",
            p("c", now.AddHours(-4)));
        // Not due: touched a minute ago.
        await database.UpsertSessionAsync(Record("alive-idle", 3600, "lastActivity", owner: "alice"));
        await database.ExecuteAsync("UPDATE sessions SET created_at = @c, last_activity_at = @a WHERE id = 'alive-idle'",
            p("c", now.AddDays(-1)), p("a", now.AddMinutes(-1)));
        // Not due: from start, created a minute ago, even though it was never touched since.
        await database.UpsertSessionAsync(Record("alive-start", 3600, "start", owner: "alice"));
        await database.ExecuteAsync("UPDATE sessions SET created_at = @c, last_activity_at = @c WHERE id = 'alive-start'",
            p("c", now.AddMinutes(-1)));
        // Never: no deadline, however old.
        await database.UpsertSessionAsync(Record("kept", null, null, owner: "alice"));
        await database.ExecuteAsync("UPDATE sessions SET created_at = @c, last_activity_at = @c WHERE id = 'kept'",
            p("c", now.AddYears(-1)));

        var due = await database.Sessions.ListExpiredAsync(now, 10);

        Assert.Equal(["due-legacy", "due-start", "due-idle"], due.Select(r => r.Id).ToArray());
        Assert.Equal(["carol", "alice", "bob"], due.Select(r => r.Owner).ToArray());

        var capped = await database.Sessions.ListExpiredAsync(now, 1);
        Assert.Single(capped);
    }

    [PostgreSqlFact]
    public async Task ExpiryLock_IsExclusivePerSession_AndIndependentOfTheBrowserLock()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var locks = new PostgresSessionExpiryLock(database.Configuration);
        var browserLocks = new Browser.PostgresBrowserSessionLock(database.Configuration);

        await using var first = await locks.TryAcquireAsync("s-1");
        Assert.NotNull(first);
        // A second replica asking for the same session is told no, at once.
        Assert.Null(await locks.TryAcquireAsync("s-1"));
        // Another session is unaffected.
        await using var other = await locks.TryAcquireAsync("s-2");
        Assert.NotNull(other);
        // The browser reconcile's blocking lock on the same id lives in another key space, so
        // holding the expiry lock does not stall it (and this call would hang if it did).
        await using var browser = await browserLocks.AcquireAsync("s-1");

        await first.DisposeAsync();
        await using var again = await locks.TryAcquireAsync("s-1");
        Assert.NotNull(again);
    }

    private static SessionRecord Record(string id, int? seconds, string? from, string owner = "alice") => new()
    {
        Id = id,
        Owner = owner,
        Title = "Build triage",
        Mode = SessionMode.Interactive,
        Prompt = "triage the failing build",
        SystemPrompt = "You review, you do not commit.",
        CredentialId = "acct-1",
        AgentSessionId = $"agent-{id}",
        CallbackToken = $"callback-{id}",
        AutoDeleteAfterSeconds = seconds,
        AutoDeleteFrom = from
    };
}
