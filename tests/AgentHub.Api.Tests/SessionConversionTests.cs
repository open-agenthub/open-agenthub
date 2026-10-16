using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The rules of continuing an autonomous session interactively (docs/session-mode-conversion.md),
/// cluster-free. The service applies the plan and then runs the ordinary resume, which builds its
/// request from the record — so what these tests pin on the record is what the pod will see.
/// </summary>
public class SessionConversionTests
{
    [Theory]
    [InlineData(SessionMode.Autonomous, SessionStatus.Succeeded, true)]
    [InlineData(SessionMode.Autonomous, SessionStatus.Failed, true)]
    [InlineData(SessionMode.Autonomous, SessionStatus.Paused, true)]
    // A running pod is still working on the autonomous run; converting underneath it would fork
    // the conversation.
    [InlineData(SessionMode.Autonomous, SessionStatus.Running, false)]
    [InlineData(SessionMode.Autonomous, SessionStatus.Pending, false)]
    // Already interactive, or no single conversation to continue.
    [InlineData(SessionMode.Interactive, SessionStatus.Succeeded, false)]
    [InlineData(SessionMode.Scheduled, SessionStatus.Scheduled, false)]
    [InlineData(SessionMode.Scheduled, SessionStatus.Succeeded, false)]
    public void CanConvertToInteractive_OnlyForAStoppedAutonomousSession(SessionMode mode, string phase, bool expected)
        => Assert.Equal(expected, SessionStatus.CanConvertToInteractive(mode, phase));

    [Fact]
    public void CanConvertToInteractive_NeverExceedsCanResume()
    {
        // The conversion is a resume with a changed record. A phase one predicate accepts and the
        // other refuses would offer a card whose button answers 409.
        foreach (var phase in new[]
                 {
                     SessionStatus.Pending, SessionStatus.Running, SessionStatus.Paused,
                     SessionStatus.Succeeded, SessionStatus.Failed, SessionStatus.Scheduled
                 })
        {
            if (SessionStatus.CanConvertToInteractive(SessionMode.Autonomous, phase))
                Assert.True(SessionStatus.CanResume(SessionMode.Autonomous, phase), phase);
        }
    }

    [Fact]
    public void Validate_DefaultsToTerminalWithAutoApproveOff()
    {
        var plan = SessionConversion.Validate(Autonomous(), SessionStatus.Succeeded, new ConvertSessionRequest());

        Assert.Equal(SessionUiMode.Terminal, plan.UiMode);
        // The autonomous run approved everything because nobody was there; now somebody is.
        Assert.False(plan.AutoApprove);
    }

    [Fact]
    public void Validate_KeepsAutoApproveOnlyWhenAsked()
    {
        var kept = SessionConversion.Validate(Autonomous(), SessionStatus.Paused,
            new ConvertSessionRequest { AutoApprove = true });
        Assert.True(kept.AutoApprove);

        var off = SessionConversion.Validate(Autonomous(), SessionStatus.Paused,
            new ConvertSessionRequest { AutoApprove = false });
        Assert.False(off.AutoApprove);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("interactive")]
    [InlineData(" Interactive ")]
    public void Validate_AcceptsTheInteractiveTargetInAnySpelling(string? mode)
        => Assert.NotNull(SessionConversion.Validate(Autonomous(), SessionStatus.Succeeded,
            new ConvertSessionRequest { Mode = mode }));

    [Theory]
    [InlineData("autonomous")]
    [InlineData("scheduled")]
    [InlineData("chat")]
    public void Validate_RejectsAnyOtherTargetAsABadRequest(string mode)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            SessionConversion.Validate(Autonomous(), SessionStatus.Succeeded, new ConvertSessionRequest { Mode = mode }));
        Assert.Contains("'interactive'", error.Message);
    }

    [Fact]
    public void Validate_ChatUiIsClaudeOnly_LikeOnCreate()
    {
        var claude = SessionConversion.Validate(Autonomous(AgentKind.Claude), SessionStatus.Succeeded,
            new ConvertSessionRequest { UiMode = "chat" });
        Assert.Equal(SessionUiMode.Chat, claude.UiMode);

        foreach (var agent in new[] { AgentKind.Codex, AgentKind.Cursor, AgentKind.OpenClaw })
        {
            var error = Assert.Throws<ArgumentException>(() =>
                SessionConversion.Validate(Autonomous(agent), SessionStatus.Succeeded,
                    new ConvertSessionRequest { UiMode = "chat" }));
            Assert.Contains("interactive Claude", error.Message);
        }
    }

    [Theory]
    [InlineData(SessionMode.Interactive, "already interactive")]
    [InlineData(SessionMode.Scheduled, "scheduled")]
    public void Validate_RefusesSessionsOfAnotherModeAsAConflict(SessionMode mode, string reason)
    {
        var record = Autonomous();
        record.Mode = mode;

        var error = Assert.Throws<InvalidOperationException>(() =>
            SessionConversion.Validate(record, SessionStatus.Succeeded, new ConvertSessionRequest()));
        Assert.Contains(reason, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SessionStatus.Running)]
    [InlineData(SessionStatus.Pending)]
    public void Validate_RefusesALiveSessionAsAConflict(string phase)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SessionConversion.Validate(Autonomous(), phase, new ConvertSessionRequest()));
        Assert.Contains("Pause", error.Message);
    }

    [Fact]
    public void Validate_ChecksTheModeBeforeTheUiMode()
    {
        // A 409 for the state, not a 400 for a field that would be fine once the session stops.
        Assert.Throws<InvalidOperationException>(() =>
            SessionConversion.Validate(Autonomous(AgentKind.Codex), SessionStatus.Running,
                new ConvertSessionRequest { UiMode = "chat" }));
    }

    [Fact]
    public void Apply_RewritesTheRecordTheResumeIsBuiltFrom()
    {
        var record = Autonomous(AgentKind.Claude);
        record.AutoApprove = true;
        var plan = SessionConversion.Validate(record, SessionStatus.Succeeded,
            new ConvertSessionRequest { UiMode = "chat" });

        SessionConversion.Apply(record, plan);

        // ResumeSessionAsync copies Mode, UiMode and AutoApprove from the record into the create
        // request, so these three are exactly what AGENTHUB_MODE, AGENTHUB_UI_MODE and
        // AGENTHUB_AUTO_APPROVE will carry.
        Assert.Equal(SessionMode.Interactive, record.Mode);
        Assert.Equal(SessionUiMode.Chat, record.UiMode);
        Assert.False(record.AutoApprove);
        Assert.Equal(SessionMode.Autonomous, record.ConvertedFrom);
        // The task stays on the record: the drivers skip it on a restored resume, and a session
        // whose archive is missing starts the task over instead of coming up idle.
        Assert.Equal("triage the failing build", record.Prompt);
        Assert.Equal("agent-session", record.AgentSessionId);
    }

    private static SessionRecord Autonomous(AgentKind agent = AgentKind.Claude) => new()
    {
        Id = "s-1",
        Owner = "alice",
        Title = "Build triage",
        Mode = SessionMode.Autonomous,
        Agent = agent,
        AuthMode = AgentAuthMode.ApiKey,
        Prompt = "triage the failing build",
        AutoApprove = true,
        AgentSessionId = "agent-session",
        CallbackToken = "callback"
    };
}
