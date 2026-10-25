using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Usage;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>Who pays for a session's tokens (UsageBilling) and the label a Claude pod carries.</summary>
public class UsageBillingTests
{
    [Theory]
    [InlineData("Subscription", "api", "subscription")] // the record wins over a pod label
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
    public void ForPod(AgentAuthMode authMode, SessionMode mode, bool hasKey, bool hasLogin, string expected)
        => Assert.Equal(expected, UsageBilling.ForPod(authMode, mode, hasKey, hasLogin));

    [Fact]
    public void ClaudePod_CarriesItsBillingLabelInTheOtelResourceAttributes()
    {
        var record = new SessionRecord
        {
            Id = "session-id", Owner = "owner", Title = "Session", Mode = SessionMode.Autonomous,
            Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Auto, CallbackToken = "callback-token"
        };
        var context = new PodBuildContext
        {
            Owner = "owner",
            CredentialsSecretName = "creds-owner",
            ClaudeCredentialSecretName = "claude-owner",
            CodexCredentialSecretName = "codex-owner",
            CursorCredentialSecretName = "cursor-owner",
            OpenClawCredentialSecretName = "openclaw-owner",
            OpenCodeCredentialSecretName = "opencode-owner",
            CallbackUrl = "http://callback/internal/sessions/session-id",
            StatePutUrl = "http://s3/state-put",
            StateGetUrl = "",
            ScrollbackPutUrl = "http://s3/scroll-put",
            TranscriptPutUrl = "http://s3/transcript-put",
            HasSelectedApiKey = true,
            HasSelectedSubscriptionCredential = true,
            RuntimeImages = new AgentRuntimeImages(
                "runtime-claude", "runtime-codex", "runtime-cursor", "runtime-openclaw", "runtime-opencode", "Always"),
            Runtime = new AgentPodRuntimeSettings
            {
                AgentPort = 7681, GitCloneImage = "git-clone", MaxCpu = "2", MaxMemory = "4Gi", TelemetryEnabled = true
            }
        };

        var pod = AgentPodSpecFactory.Build(record,
            new CreateSessionRequest { Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Auto, Mode = SessionMode.Autonomous }, context);

        var attributes = Assert.Single(pod.Containers[0].Env, e => e.Name == "OTEL_RESOURCE_ATTRIBUTES").Value;
        Assert.Equal("agenthub.session_id=session-id,agenthub.owner=owner,agenthub.billing=api", attributes);
    }

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
