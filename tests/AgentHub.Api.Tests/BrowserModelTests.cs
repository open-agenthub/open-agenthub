using AgentHub.Api.Browser;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class BrowserModelTests
{
    [Fact]
    public void PendingLease_StartsWithoutPodOrFailure()
    {
        var hash = new byte[] { 1, 2, 3 };
        var lease = BrowserLease.Pending("session-1", "lease-1", hash);

        Assert.Equal(BrowserPhase.Pending, lease.Phase);
        Assert.Equal("session-1", lease.SessionId);
        Assert.Equal("lease-1", lease.LeaseId);
        Assert.Same(hash, lease.TokenHash);
        Assert.Null(lease.PodIp);
        Assert.Null(lease.FailureCode);
    }

    [Fact]
    public void DefaultSummary_IsStoppedAtConfiguredDefaultSize()
    {
        var summary = BrowserSummary.Stopped;

        Assert.Equal(BrowserPhase.Stopped, summary.Phase);
        Assert.Equal(1440, summary.ScreenWidth);
        Assert.Equal(900, summary.ScreenHeight);
    }
}
