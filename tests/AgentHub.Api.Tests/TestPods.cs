using System.Net;
using AgentHub.Api.Browser;
using AgentHub.Api.Services;

namespace AgentHub.Api.Tests;

/// <summary>The real <see cref="AgentPodRequests"/> over a test client, with the pod's identity
/// answered by the test instead of the Kubernetes API.</summary>
internal static class TestPods
{
    public static IAgentPodRequests Live(HttpClient http) => new AgentPodRequests(http, new Fixed(true));

    public static IAgentPodRequests Using(HttpClient http, IAgentPodIdentityResolver identity) =>
        new AgentPodRequests(http, identity);

    private sealed class Fixed(bool live) : IAgentPodIdentityResolver
    {
        public Task<bool> IsLiveSessionPodAsync(string sessionId, IPAddress sourceIp, CancellationToken ct = default) =>
            Task.FromResult(live);
    }
}
