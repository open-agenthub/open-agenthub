using AgentHub.Api.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public class AutoApproveDefaultTests
{
    // Nobody is watching an unattended session, so a permission prompt there has no one to
    // answer it: the agent stalls on its first tool call and finishes having done nothing,
    // while reporting success. Observed in production — an autonomous triage session ended
    // with "This session can't prompt for approval, so I never saw a single alert."
    [Theory]
    [InlineData(SessionMode.Autonomous, true)]
    [InlineData(SessionMode.Scheduled, true)]
    // Interactive sessions have a human present, so each request is still worth asking about.
    [InlineData(SessionMode.Interactive, false)]
    public void Unattended_modes_approve_automatically_by_default(SessionMode mode, bool expected)
        => Assert.Equal(expected, CreateSessionRequest.AutoApproveFor(null, mode));

    [Theory]
    [InlineData(SessionMode.Autonomous)]
    [InlineData(SessionMode.Scheduled)]
    [InlineData(SessionMode.Interactive)]
    public void An_explicit_choice_always_wins_over_the_mode_default(SessionMode mode)
    {
        Assert.True(CreateSessionRequest.AutoApproveFor(true, mode));
        // Explicitly gating an autonomous session must stay possible — the default is a
        // convenience, not a policy the caller cannot override.
        Assert.False(CreateSessionRequest.AutoApproveFor(false, mode));
    }
}
