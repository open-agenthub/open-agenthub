using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The system prompt on <c>PATCH /api/sessions/{id}</c> and on duplication. The create-side
/// normalization is covered elsewhere; these pin the update conventions the dialogs rely on.
/// </summary>
public sealed class SessionSystemPromptUpdateTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PatchBody_DistinguishesOmittedFromExplicitClear()
    {
        // The edit dialog sends "" to remove the prompt and leaves the key out to keep it; a
        // deserializer that folded "" into null would make "clear" indistinguishable from "keep".
        var omitted = JsonSerializer.Deserialize<UpdateSessionRequest>("{\"title\":\"Renamed\"}", Web)!;
        var cleared = JsonSerializer.Deserialize<UpdateSessionRequest>("{\"systemPrompt\":\"\"}", Web)!;
        var set = JsonSerializer.Deserialize<UpdateSessionRequest>("{\"systemPrompt\":\"  be terse \"}", Web)!;

        Assert.Null(omitted.SystemPrompt);
        Assert.Equal("", cleared.SystemPrompt);
        Assert.Null(SessionSystemPrompt.Normalize(cleared.SystemPrompt));
        Assert.Equal("be terse", SessionSystemPrompt.Normalize(set.SystemPrompt));
    }

    [Fact]
    public void Patch_OverTheCap_IsRejectedWithTheCreateLimit()
    {
        var request = new UpdateSessionRequest { SystemPrompt = new string('x', SessionSystemPrompt.MaxLength + 1) };

        var error = Assert.Throws<ArgumentException>(() => SessionSystemPrompt.Normalize(request.SystemPrompt));
        Assert.Contains(SessionSystemPrompt.MaxLength.ToString(), error.Message);
    }

    [Theory]
    [InlineData("be terse")]
    [InlineData("")]
    public void Scheduled_RejectsSettingOrClearingTheSystemPrompt(string systemPrompt)
    {
        var scheduled = Session(SessionMode.Scheduled);

        Assert.Throws<ArgumentException>(() =>
            SessionUpdateValidator.Validate(scheduled, new UpdateSessionRequest { SystemPrompt = systemPrompt }));
        // Omitting it keeps the title-only update a scheduled session is allowed.
        SessionUpdateValidator.Validate(scheduled, new UpdateSessionRequest { Title = "Renamed" });
    }

    [Theory]
    [InlineData(SessionMode.Interactive)]
    [InlineData(SessionMode.Autonomous)]
    public void NonScheduled_AcceptsTheSystemPrompt(SessionMode mode)
    {
        SessionUpdateValidator.Validate(Session(mode), new UpdateSessionRequest { SystemPrompt = "be terse" });
        SessionUpdateValidator.Validate(Session(mode), new UpdateSessionRequest { SystemPrompt = "" });
    }

    [Fact]
    public void Duplicate_CopiesTheSystemPrompt()
    {
        var source = Session(SessionMode.Interactive);
        source.SystemPrompt = "You review, you do not commit.";

        var copy = SessionDuplication.CopyableRequest(source, new("Copy", null, false));

        Assert.Equal("You review, you do not commit.", copy.SystemPrompt);
    }

    [Fact]
    public void Duplicate_LetsTheRequestReplaceOrDropTheCopiedSystemPrompt()
    {
        var source = Session(SessionMode.Interactive);
        source.SystemPrompt = "original rules";

        var replaced = SessionDuplication.CopyableRequest(source, new("Copy", null, false, SystemPrompt: "new rules"));
        var dropped = SessionDuplication.CopyableRequest(source, new("Copy", null, false, SystemPrompt: ""));

        Assert.Equal("new rules", replaced.SystemPrompt);
        // Create-side normalization turns the empty override into "no prompt".
        Assert.Null(SessionSystemPrompt.Normalize(dropped.SystemPrompt));
    }

    private static SessionRecord Session(SessionMode mode) => new()
    {
        Id = "session", Owner = "alice", Title = "Session", Mode = mode,
        Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription,
        AgentSessionId = "thread", CallbackToken = "token"
    };
}
