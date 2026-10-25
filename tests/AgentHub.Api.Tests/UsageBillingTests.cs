using AgentHub.Api.Models;
using AgentHub.Api.Usage;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>Who pays for a session's tokens (UsageBilling) and the label a Claude pod carries.</summary>
public class UsageBillingTests
{
    [Theory]
    [InlineData("Subscription", "api", "subscription")] // the auth mode wins over a stored label
    [InlineData("ApiKey", "subscription", "api")]
    [InlineData("CustomEndpoint", null, "custom")]
    [InlineData("Auto", "api", "api")]
    [InlineData("Auto", "subscription", "subscription")]
    [InlineData("Auto", null, "unknown")]
    [InlineData("Auto", "free", "unknown")]
    [InlineData(null, null, "unknown")]
    public void Classify(string? authMode, string? podLabel, string expected)
        => Assert.Equal(expected, UsageBilling.Classify(authMode, podLabel));

    [Theory]
    [InlineData(AgentAuthMode.Subscription, SessionMode.Autonomous, true, true, "subscription")]
    [InlineData(AgentAuthMode.ApiKey, SessionMode.Interactive, true, false, "api")]
    // claude -p prefers ANTHROPIC_API_KEY over a stored login (verified on 2.1.287).
    [InlineData(AgentAuthMode.Auto, SessionMode.Autonomous, true, true, "api")]
    [InlineData(AgentAuthMode.Auto, SessionMode.Scheduled, false, true, "subscription")]
    // The interactive TUI asks the person whether to use a key: the hub cannot know the answer.
    [InlineData(AgentAuthMode.Auto, SessionMode.Interactive, true, true, "unknown")]
    [InlineData(AgentAuthMode.Auto, SessionMode.Interactive, true, false, "api")]
    [InlineData(AgentAuthMode.Auto, SessionMode.Interactive, false, true, "unknown")]
    public void ForPod(AgentAuthMode authMode, SessionMode mode, bool hasKey, bool hasLogin, string expected)
        => Assert.Equal(expected, UsageBilling.ForPod(authMode, mode, hasKey, hasLogin));

    [Fact]
    public void Report_SinceAddsOnlyTheDifference_AndCountsARestartInFull()
    {
        var first = new AgentUsageReport(400, 50, 600, 0, 20, 1, null);
        var second = new AgentUsageReport(800, 100, 1200, 0, 40, 2, null);

        Assert.Equal(first, second.Since(first) with { });
        Assert.Equal(new AgentUsageReport(0, 0, 0, 0, 0, 0, null), first.Since(first));
        // A fresh pod without the archived state starts its record over: all of it is new.
        Assert.Equal(first, first.Since(second));
        Assert.Equal(0.5, new AgentUsageReport(1, 1, 0, 0, 0, 1, 1.5).Since(new AgentUsageReport(0, 0, 0, 0, 0, 0, 1.0)).CostUsd);
    }
}
