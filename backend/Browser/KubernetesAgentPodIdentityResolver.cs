using System.Net;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Browser;

public sealed class KubernetesAgentPodIdentityResolver : IAgentPodIdentityResolver
{
    private readonly Kubernetes _kubernetes;
    private readonly string _namespace;

    public KubernetesAgentPodIdentityResolver(IConfiguration configuration)
    {
        _namespace = configuration["AgentHub:Namespace"] ?? "agenthub-sessions";
        var clientConfiguration = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _kubernetes = new Kubernetes(clientConfiguration);
    }

    public async Task<bool> IsLiveSessionPodAsync(string sessionId, IPAddress sourceIp,
        CancellationToken ct = default)
    {
        var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(
            _namespace,
            labelSelector: $"agenthub.dev/session={sessionId},agenthub.dev/component=agent",
            fieldSelector: $"metadata.name=session-{sessionId}",
            cancellationToken: ct);
        return MatchesLivePod(pods.Items, sessionId, sourceIp);
    }

    public static bool MatchesLivePod(IEnumerable<V1Pod> pods, string sessionId,
        IPAddress sourceIp) => pods.Any(pod =>
            pod.Metadata?.DeletionTimestamp is null &&
            string.Equals(pod.Metadata?.Name, $"session-{sessionId}", StringComparison.Ordinal) &&
            string.Equals(pod.Status?.Phase, "Running", StringComparison.Ordinal) &&
            pod.Metadata?.Labels is { } labels &&
            labels.TryGetValue("agenthub.dev/session", out var podSession) &&
            string.Equals(podSession, sessionId, StringComparison.Ordinal) &&
            labels.TryGetValue("agenthub.dev/component", out var component) &&
            string.Equals(component, "agent", StringComparison.Ordinal) &&
            IPAddress.TryParse(pod.Status?.PodIP, out var podIp) &&
            IpAddressNormalization.Equals(sourceIp, podIp));
}
