using System.Net;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Network;

public interface INetworkPolicyClient
{
    /// <summary>Creates the policies; an already-existing policy (same name) is fine —
    /// grants are deterministic per session+direction+port, so a conflict means the
    /// grant was applied before.</summary>
    Task CreateAsync(IReadOnlyList<V1NetworkPolicy> policies, CancellationToken ct = default);

    /// <summary>Deletes every port-request policy of the session (label-selected).</summary>
    Task DeleteBySessionAsync(string namespaceName, string sessionId, CancellationToken ct = default);
}

public sealed class KubernetesNetworkPolicyClient : INetworkPolicyClient
{
    private readonly Kubernetes _k8s;

    public KubernetesNetworkPolicyClient()
    {
        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _k8s = new Kubernetes(config);
    }

    public async Task CreateAsync(IReadOnlyList<V1NetworkPolicy> policies, CancellationToken ct = default)
    {
        foreach (var policy in policies)
        {
            try
            {
                await _k8s.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                    policy, policy.Metadata.NamespaceProperty, cancellationToken: ct);
            }
            catch (k8s.Autorest.HttpOperationException error)
                when (error.Response.StatusCode == HttpStatusCode.Conflict)
            {
                // Deterministic names: an existing policy IS this grant (re-poll, replica race).
            }
        }
    }

    public async Task DeleteBySessionAsync(string namespaceName, string sessionId, CancellationToken ct = default)
    {
        await _k8s.NetworkingV1.DeleteCollectionNamespacedNetworkPolicyAsync(namespaceName,
            labelSelector: $"{NetworkPolicyFactory.ResourceSelector},agenthub.dev/session={sessionId}",
            cancellationToken: ct);
    }
}
