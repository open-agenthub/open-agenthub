using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace AgentHub.Api.Containers;

/// <summary>The cluster calls behind session containers, behind an interface so the
/// orchestration in <see cref="SessionContainerService"/> is testable without a cluster.</summary>
public interface ISessionContainerCluster
{
    Task<V1Namespace?> GetNamespaceAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<V1Namespace>> ListNamespacesAsync(string labelSelector, CancellationToken ct = default);
    /// <summary>Creates the object; one that already exists under the same name is left as is.
    /// Every name is deterministic per session, so a conflict is a second replica or a retry
    /// that got there first.</summary>
    Task CreateNamespaceAsync(V1Namespace value, CancellationToken ct = default);
    Task CreateRoleBindingAsync(V1RoleBinding value, CancellationToken ct = default);
    Task CreateQuotaAsync(V1ResourceQuota value, CancellationToken ct = default);
    Task CreateLimitRangeAsync(V1LimitRange value, CancellationToken ct = default);
    Task CreateNetworkPolicyAsync(V1NetworkPolicy value, CancellationToken ct = default);
    /// <summary>Unlike the others, a pod that exists already is reported (false), because a
    /// second run under the same name is the agent's mistake, not a retry.</summary>
    Task<bool> CreatePodAsync(V1Pod value, CancellationToken ct = default);
    Task CreateServiceAsync(V1Service value, CancellationToken ct = default);
    Task<IReadOnlyList<V1Pod>> ListPodsAsync(string namespaceName, string labelSelector, CancellationToken ct = default);
    Task<string?> ReadLogAsync(string namespaceName, string pod, int tailLines, int limitBytes, bool previous,
        CancellationToken ct = default);
    /// <summary>Deletes; a missing object counts as deleted.</summary>
    Task DeletePodAsync(string namespaceName, string name, CancellationToken ct = default);
    Task DeleteServiceAsync(string namespaceName, string name, CancellationToken ct = default);
    Task DeleteNamespaceAsync(string name, CancellationToken ct = default);
    Task DeleteNetworkPoliciesAsync(string namespaceName, string labelSelector, CancellationToken ct = default);
}

public sealed class KubernetesSessionContainerCluster : ISessionContainerCluster
{
    private readonly Kubernetes _k8s;

    public KubernetesSessionContainerCluster()
    {
        var config = KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile();
        _k8s = new Kubernetes(config);
    }

    public async Task<V1Namespace?> GetNamespaceAsync(string name, CancellationToken ct = default)
    {
        try { return await _k8s.CoreV1.ReadNamespaceAsync(name, cancellationToken: ct); }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    public async Task<IReadOnlyList<V1Namespace>> ListNamespacesAsync(string labelSelector, CancellationToken ct = default) =>
        (await _k8s.CoreV1.ListNamespaceAsync(labelSelector: labelSelector, cancellationToken: ct)).Items.ToList();

    public Task CreateNamespaceAsync(V1Namespace value, CancellationToken ct = default) =>
        IgnoreConflict(() => _k8s.CoreV1.CreateNamespaceAsync(value, cancellationToken: ct));

    public Task CreateRoleBindingAsync(V1RoleBinding value, CancellationToken ct = default) =>
        IgnoreConflict(() => _k8s.RbacAuthorizationV1.CreateNamespacedRoleBindingAsync(
            value, value.Metadata.NamespaceProperty, cancellationToken: ct));

    public Task CreateQuotaAsync(V1ResourceQuota value, CancellationToken ct = default) =>
        IgnoreConflict(() => _k8s.CoreV1.CreateNamespacedResourceQuotaAsync(
            value, value.Metadata.NamespaceProperty, cancellationToken: ct));

    public Task CreateLimitRangeAsync(V1LimitRange value, CancellationToken ct = default) =>
        IgnoreConflict(() => _k8s.CoreV1.CreateNamespacedLimitRangeAsync(
            value, value.Metadata.NamespaceProperty, cancellationToken: ct));

    public Task CreateNetworkPolicyAsync(V1NetworkPolicy value, CancellationToken ct = default) =>
        IgnoreConflict(() => _k8s.NetworkingV1.CreateNamespacedNetworkPolicyAsync(
            value, value.Metadata.NamespaceProperty, cancellationToken: ct));

    public async Task<bool> CreatePodAsync(V1Pod value, CancellationToken ct = default)
    {
        try
        {
            await _k8s.CoreV1.CreateNamespacedPodAsync(value, value.Metadata.NamespaceProperty, cancellationToken: ct);
            return true;
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Conflict) { return false; }
    }

    public Task CreateServiceAsync(V1Service value, CancellationToken ct = default) =>
        IgnoreConflict(() => _k8s.CoreV1.CreateNamespacedServiceAsync(
            value, value.Metadata.NamespaceProperty, cancellationToken: ct));

    public async Task<IReadOnlyList<V1Pod>> ListPodsAsync(string namespaceName, string labelSelector, CancellationToken ct = default)
    {
        try
        {
            return (await _k8s.CoreV1.ListNamespacedPodAsync(namespaceName, labelSelector: labelSelector, cancellationToken: ct))
                .Items.ToList();
        }
        catch (HttpOperationException error) when (error.Response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            // Forbidden as well: until the backend's RoleBinding exists in a namespace it cannot
            // list there, and a namespace that has none yet holds no helpers either.
            return [];
        }
    }

    public async Task<string?> ReadLogAsync(string namespaceName, string pod, int tailLines, int limitBytes, bool previous,
        CancellationToken ct = default)
    {
        try
        {
            await using var stream = await _k8s.CoreV1.ReadNamespacedPodLogAsync(pod, namespaceName,
                container: "main", tailLines: tailLines, limitBytes: limitBytes, previous: previous, cancellationToken: ct);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (HttpOperationException error) when (error.Response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            // 400 is what the API server answers while the container has not started yet, or
            // for `previous` when there was no earlier run.
            return null;
        }
    }

    public Task DeletePodAsync(string namespaceName, string name, CancellationToken ct = default) =>
        IgnoreNotFound(() => _k8s.CoreV1.DeleteNamespacedPodAsync(name, namespaceName, gracePeriodSeconds: 5, cancellationToken: ct));

    public Task DeleteServiceAsync(string namespaceName, string name, CancellationToken ct = default) =>
        IgnoreNotFound(() => _k8s.CoreV1.DeleteNamespacedServiceAsync(name, namespaceName, cancellationToken: ct));

    public Task DeleteNamespaceAsync(string name, CancellationToken ct = default) =>
        IgnoreNotFound(() => _k8s.CoreV1.DeleteNamespaceAsync(name, propagationPolicy: "Background", cancellationToken: ct));

    public async Task DeleteNetworkPoliciesAsync(string namespaceName, string labelSelector, CancellationToken ct = default) =>
        await _k8s.NetworkingV1.DeleteCollectionNamespacedNetworkPolicyAsync(namespaceName,
            labelSelector: labelSelector, cancellationToken: ct);

    /// <returns>Whether this call created the object; false = it existed already.</returns>
    private static async Task<bool> IgnoreConflict<T>(Func<Task<T>> create)
    {
        try
        {
            await create();
            return true;
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    /// <returns>Whether this call deleted the object; false = it was gone already.</returns>
    private static async Task<bool> IgnoreNotFound<T>(Func<Task<T>> delete)
    {
        try
        {
            await delete();
            return true;
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }
}
