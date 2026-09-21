using System.Net;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Browser;

public sealed record BrowserPodSnapshot(string? PodIp, bool Ready, string? FailureCode, string? LeaseId = null);

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
        foreach (var policy in resources.Policies) await CreatePolicyAsync(policy, ct);
        try
        {
            await _k8s.CoreV1.CreateNamespacedPodAsync(resources.Pod,
                resources.Pod.Metadata.NamespaceProperty, cancellationToken: ct);
        }
        catch (k8s.Autorest.HttpOperationException error)
            when (error.Response.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await _k8s.CoreV1.ReadNamespacedPodAsync(
                resources.Pod.Metadata.Name, resources.Pod.Metadata.NamespaceProperty,
                cancellationToken: ct);
            RequireExpectedLease(existing.Metadata,
                resources.Pod.Metadata.Labels[BrowserPodSpecFactory.LeaseLabel], "pod");
        }
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
            return Snapshot(pod, false, "image_pull");
        if (pod.Status?.Conditions?.Any(condition => condition.Type == "PodScheduled" &&
                condition.Status == "False" && condition.Reason == "Unschedulable") == true)
            return Snapshot(pod, false, "unschedulable");

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
                return Snapshot(pod, false, failure);
        }

        var ready = pod.Status?.Phase == "Running" &&
            pod.Status.Conditions?.Any(condition => condition.Type == "Ready" && condition.Status == "True") == true;
        return Snapshot(pod, ready, null);
    }

    private static BrowserPodSnapshot Snapshot(V1Pod pod, bool ready, string? failureCode)
    {
        string? leaseId = null;
        pod.Metadata?.Labels?.TryGetValue(BrowserPodSpecFactory.LeaseLabel, out leaseId);
        return new BrowserPodSnapshot(pod.Status?.PodIP, ready, failureCode, leaseId);
    }
    public async Task DeleteAsync(string namespaceName, string sessionId, CancellationToken ct = default)
    {
        var name = $"browser-{sessionId}";
        await IgnoreNotFoundAsync(() => _k8s.CoreV1.DeleteNamespacedPodAsync(
            name, namespaceName, gracePeriodSeconds: 50, cancellationToken: ct));
        // Keep callback egress and the lease alive while SIGTERM triggers the final cookie checkpoint.
        var deadline = DateTime.UtcNow.AddSeconds(55);
        while (DateTime.UtcNow < deadline &&
               await GetAsync(namespaceName, sessionId, ct) is not null)
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        foreach (var suffix in BrowserPodSpecFactory.PolicySuffixes)
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
            when (error.Response.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await _k8s.NetworkingV1.ReadNamespacedNetworkPolicyAsync(
                policy.Metadata.Name, policy.Metadata.NamespaceProperty, cancellationToken: ct);
            RequireExpectedLease(existing.Metadata,
                policy.Metadata.Labels[BrowserPodSpecFactory.LeaseLabel], "network policy");
        }
    }

    public static bool HasExpectedLease(V1ObjectMeta? metadata, string expectedLease) =>
        metadata?.Labels is { } labels &&
        labels.TryGetValue(BrowserPodSpecFactory.LeaseLabel, out var actualLease) &&
        string.Equals(actualLease, expectedLease, StringComparison.Ordinal);

    private static void RequireExpectedLease(V1ObjectMeta? metadata, string expectedLease,
        string resourceKind)
    {
        if (!HasExpectedLease(metadata, expectedLease))
            throw new InvalidOperationException(
                $"Existing browser {resourceKind} belongs to a different lease generation.");
    }
    private static async Task IgnoreNotFoundAsync(Func<Task> action)
    {
        try { await action(); }
        catch (k8s.Autorest.HttpOperationException error)
            when (error.Response.StatusCode == HttpStatusCode.NotFound) { }
    }
}