using System.Net;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Browser;

public sealed record BrowserPodSnapshot(string? PodIp, bool Ready, string? FailureCode);

public interface IBrowserClusterClient
{
    Task CreateAsync(BrowserPodResources resources, CancellationToken ct = default);
    Task<BrowserPodSnapshot?> GetAsync(string namespaceName, string sessionId,
        CancellationToken ct = default);
    Task DeleteAsync(string namespaceName, string sessionId, CancellationToken ct = default);
    Task<IReadOnlyCollection<string>> ListSessionIdsAsync(string namespaceName,
        CancellationToken ct = default);
}

public sealed class KubernetesBrowserClusterClient : IBrowserClusterClient
{
    private readonly Kubernetes _k8s;

    public KubernetesBrowserClusterClient()
    {
        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _k8s = new Kubernetes(config);
    }

    public async Task CreateAsync(BrowserPodResources resources, CancellationToken ct = default)
    {
        await CreatePolicyAsync(resources.CdpIngress, ct);
        await CreatePolicyAsync(resources.CdpEgress, ct);
        await CreatePolicyAsync(resources.BrowserEgress, ct);
        await CreatePolicyAsync(resources.VncIngress, ct);
        try
        {
            await _k8s.CoreV1.CreateNamespacedPodAsync(resources.Pod,
                resources.Pod.Metadata.NamespaceProperty, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException error)
            when (error.Response.StatusCode == HttpStatusCode.Conflict) { }
    }

    public async Task<BrowserPodSnapshot?> GetAsync(string namespaceName, string sessionId,
        CancellationToken ct = default)
    {
        V1Pod pod;
        try
        {
            pod = await _k8s.CoreV1.ReadNamespacedPodAsync(
                $"browser-{sessionId}", namespaceName, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException error)
            when (error.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var waitingReasons = pod.Status?.ContainerStatuses?
            .Select(status => status.State?.Waiting?.Reason)
            .Where(reason => reason is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (waitingReasons.Contains("ErrImagePull") || waitingReasons.Contains("ImagePullBackOff"))
            return new BrowserPodSnapshot(pod.Status?.PodIP, false, "image_pull");
        if (pod.Status?.Conditions?.Any(condition => condition.Type == "PodScheduled" &&
                condition.Status == "False" && condition.Reason == "Unschedulable") == true)
            return new BrowserPodSnapshot(pod.Status?.PodIP, false, "unschedulable");

        var terminationDetails = pod.Status?.ContainerStatuses?
            .SelectMany(status => new[]
            {
                status.State?.Terminated?.Reason,
                status.State?.Terminated?.Message
            })
            .Where(detail => !string.IsNullOrWhiteSpace(detail))
            .ToArray() ?? [];
        foreach (var failure in new[] { "cdp_unavailable", "vnc_unavailable" })
        {
            if (terminationDetails.Any(detail => detail!.Contains(failure, StringComparison.OrdinalIgnoreCase)))
                return new BrowserPodSnapshot(pod.Status?.PodIP, false, failure);
        }

        var ready = pod.Status?.Phase == "Running" &&
            pod.Status.Conditions?.Any(condition => condition.Type == "Ready" && condition.Status == "True") == true;
        return new BrowserPodSnapshot(pod.Status?.PodIP, ready, null);
    }

    public async Task DeleteAsync(string namespaceName, string sessionId, CancellationToken ct = default)
    {
        var name = $"browser-{sessionId}";
        await IgnoreNotFoundAsync(() => _k8s.CoreV1.DeleteNamespacedPodAsync(
            name, namespaceName, gracePeriodSeconds: 15, cancellationToken: ct));
        // Keep callback egress and the lease alive while SIGTERM triggers the final cookie checkpoint.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline &&
               await GetAsync(namespaceName, sessionId, ct) is not null)
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        foreach (var suffix in new[] { "cdp-in", "cdp-out", "egress", "vnc-in" })
            await IgnoreNotFoundAsync(() => _k8s.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(
                $"{name}-{suffix}", namespaceName, cancellationToken: ct));
    }

    public async Task<IReadOnlyCollection<string>> ListSessionIdsAsync(
        string namespaceName, CancellationToken ct = default)
    {
        var pods = await _k8s.CoreV1.ListNamespacedPodAsync(namespaceName,
            labelSelector: "agenthub.dev/component=browser", cancellationToken: ct);
        var policies = await _k8s.NetworkingV1.ListNamespacedNetworkPolicyAsync(namespaceName,
            labelSelector: "agenthub.dev/browser-resource=true", cancellationToken: ct);
        return pods.Items.Select(pod => pod.Metadata.Labels)
            .Concat(policies.Items.Select(policy => policy.Metadata.Labels))
            .Where(labels => labels?.ContainsKey("agenthub.dev/session") == true)
            .Select(labels => labels!["agenthub.dev/session"])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task CreatePolicyAsync(V1NetworkPolicy policy, CancellationToken ct)
    {
        try
        {
            await _k8s.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
                policy, policy.Metadata.NamespaceProperty, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException error)
            when (error.Response.StatusCode == HttpStatusCode.Conflict) { }
    }

    private static async Task IgnoreNotFoundAsync(Func<Task> action)
    {
        try { await action(); }
        catch (k8s.Autorest.HttpOperationException error)
            when (error.Response.StatusCode == HttpStatusCode.NotFound) { }
    }
}