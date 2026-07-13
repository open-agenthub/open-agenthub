using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>Pause/resume status transitions — cluster-independent logic only.</summary>
public class SessionStatusTests
{
    [Theory]
    [InlineData(SessionMode.Interactive, SessionStatus.Running, true)]
    [InlineData(SessionMode.Interactive, SessionStatus.Pending, true)]
    [InlineData(SessionMode.Autonomous, SessionStatus.Running, true)]
    [InlineData(SessionMode.Interactive, SessionStatus.Paused, false)]
    [InlineData(SessionMode.Interactive, SessionStatus.Succeeded, false)]
    [InlineData(SessionMode.Scheduled, SessionStatus.Running, false)]
    public void CanPause_OnlyForLiveNonScheduled(SessionMode mode, string phase, bool expected)
        => Assert.Equal(expected, SessionStatus.CanPause(mode, phase));

    [Theory]
    [InlineData(SessionMode.Interactive, SessionStatus.Paused, true)]   // key: a paused session is resumable
    [InlineData(SessionMode.Autonomous, SessionStatus.Paused, true)]
    [InlineData(SessionMode.Interactive, SessionStatus.Succeeded, true)]
    [InlineData(SessionMode.Interactive, SessionStatus.Failed, true)]
    [InlineData(SessionMode.Interactive, SessionStatus.Running, false)]
    [InlineData(SessionMode.Interactive, SessionStatus.Pending, false)]
    [InlineData(SessionMode.Scheduled, SessionStatus.Paused, false)]
    public void CanResume_ForFinishedOrPaused(SessionMode mode, string phase, bool expected)
        => Assert.Equal(expected, SessionStatus.CanResume(mode, phase));

    [Fact]
    public void RunningToPaused_TransitionFlipsResumability()
    {
        // A live session cannot be resumed but can be paused; once paused the reverse holds.
        Assert.True(SessionStatus.CanPause(SessionMode.Interactive, SessionStatus.Running));
        Assert.False(SessionStatus.CanResume(SessionMode.Interactive, SessionStatus.Running));

        Assert.False(SessionStatus.CanPause(SessionMode.Interactive, SessionStatus.Paused));
        Assert.True(SessionStatus.CanResume(SessionMode.Interactive, SessionStatus.Paused));
    }

    [Fact]
    public void ResolvePhase_PrefersLivePodPhaseOverStoredStatus()
    {
        Assert.Equal("Running", SessionStatus.ResolvePhase("Running", SessionStatus.Paused));
        // No pod (paused/finished): the stored status is authoritative.
        Assert.Equal(SessionStatus.Paused, SessionStatus.ResolvePhase(null, SessionStatus.Paused));
        Assert.Equal(SessionStatus.Paused, SessionStatus.ResolvePhase("", SessionStatus.Paused));
    }
}
