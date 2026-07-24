using System.Net;
using AgentHub.Api.Browser;
using k8s.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class KubernetesAgentPodIdentityResolverTests
{
    [Fact]
    public void Matches_RequiresRunningAgentWithExactSessionAndIp()
    {
        var pods = new[]
        {
            Pod("session-1", "agent", "Running", "10.0.0.8"),
            Pod("session-2", "agent", "Running", "10.0.0.9"),
            Pod("session-1", "browser", "Running", "10.0.0.10")
        };

        Assert.True(KubernetesAgentPodIdentityResolver.MatchesLivePod(
            pods, "session-1", IPAddress.Parse("10.0.0.8")));
        Assert.False(KubernetesAgentPodIdentityResolver.MatchesLivePod(
            pods, "session-1", IPAddress.Parse("10.0.0.9")));
        Assert.False(KubernetesAgentPodIdentityResolver.MatchesLivePod(
            pods, "session-1", IPAddress.Parse("10.0.0.10")));
    }

    [Fact]
    public void Matches_RejectsPendingOrTerminatingPods()
    {
        var pending = Pod("session-1", "agent", "Pending", "10.0.0.8");
        var terminating = Pod("session-1", "agent", "Running", "10.0.0.8");
        terminating.Metadata.DeletionTimestamp = DateTime.UtcNow;

        Assert.False(KubernetesAgentPodIdentityResolver.MatchesLivePod(
            [pending, terminating], "session-1", IPAddress.Parse("10.0.0.8")));
    }

    private static V1Pod Pod(string session, string component, string phase, string ip) => new()
    {
        Metadata = new V1ObjectMeta(labels: new Dictionary<string, string>
        {
            ["agenthub.dev/session"] = session,
            ["agenthub.dev/component"] = component
        }),
        Status = new V1PodStatus(phase: phase, podIP: ip)
    };
}
