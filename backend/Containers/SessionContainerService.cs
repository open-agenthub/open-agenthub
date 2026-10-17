using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using k8s.Autorest;
using k8s.Models;

namespace AgentHub.Api.Containers;

/// <summary>Removes a session's helper containers; called on pause and delete.</summary>
public interface ISessionContainerCleanup
{
    Task CleanupSessionAsync(string sessionId, CancellationToken ct = default);
}

/// <summary>
/// Helper containers of a session, each in the session's own namespace
/// (docs/session-containers.md). The agent never talks to the Kubernetes API: it sends a small
/// request, and every privilege-relevant field of the pod is set here.
/// </summary>
public sealed class SessionContainerService : ISessionContainerCleanup
{
    private readonly SessionContainerOptions _options;
    private readonly AgentPodRuntimeSettings _runtime;
    private readonly string _sessionsNamespace;
    private readonly ISessionContainerCluster _cluster;
    private readonly ILogger<SessionContainerService> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SessionContainerService(IConfiguration configuration, ISessionContainerCluster cluster,
        ILogger<SessionContainerService> log, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = configuration.GetSection("SessionContainers").Get<SessionContainerOptions>() ?? new SessionContainerOptions();
        _runtime = new AgentPodRuntimeSettings { RuntimeClassName = configuration["AgentHub:RuntimeClassName"] ?? "" };
        _sessionsNamespace = configuration["AgentHub:Namespace"] ?? "agenthub-sessions";
        _cluster = cluster;
        _log = log;
        _delay = delay ?? Task.Delay;
    }

    public bool Enabled => _options.Enabled;
    public SessionContainerOptions Options => _options;

    public async Task<ContainerInfo> RunAsync(SessionRecord session, ContainerRunRequest request, CancellationToken ct = default)
    {
        RequireEnabled();
        SessionContainerSpecFactory.Validate(request, _options);
        var asRoot = await EnsureNamespaceAsync(session, ct);
        var ns = SessionContainerSpecFactory.NamespaceName(_options, session.Id);

        // The quota refuses the pod as well; this check exists to give the agent a sentence
        // instead of the API server's quota error.
        var existing = await _cluster.ListPodsAsync(ns, ComponentSelector, ct);
        if (existing.Any(p => p.Metadata.Name == request.Name))
            throw new ContainerRequestException($"a container named '{request.Name}' is already running; stop it first.");
        if (existing.Count >= _options.MaxPods)
            throw new ContainerRequestException($"this session already runs {existing.Count} containers, the limit is {_options.MaxPods}.");

        var (pod, service) = SessionContainerSpecFactory.BuildPod(_options, _runtime, session.Id, asRoot, request);
        try
        {
            if (!await RetryForbiddenAsync(() => _cluster.CreatePodAsync(pod, ct), ct))
                throw new ContainerRequestException($"a container named '{request.Name}' is already running; stop it first.");
        }
        catch (HttpOperationException error) when (error.Response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // What remains forbidden after the retries is admission — PSA or the quota — refusing
            // the pod. Its message names the field, which is what the agent needs to fix the request.
            throw new ContainerRequestException($"the cluster refused the container: {ApiMessage(error)}");
        }
        if (service is not null) await RetryForbiddenAsync(async () => { await _cluster.CreateServiceAsync(service, ct); return true; }, ct);

        _log.LogInformation("Started helper container {Name} ({Image}) for session {Session}", request.Name, request.Image, session.Id);
        return new ContainerInfo(request.Name, request.Image, "Pending", false, 0, null,
            service is null ? null : SessionContainerSpecFactory.HostName(request.Name, ns),
            request.Ports ?? [], DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListAsync(SessionRecord session, CancellationToken ct = default)
    {
        RequireEnabled();
        var ns = SessionContainerSpecFactory.NamespaceName(_options, session.Id);
        var pods = await _cluster.ListPodsAsync(ns, ComponentSelector, ct);
        return pods.Select(pod => ToInfo(pod, ns)).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<string?> LogsAsync(SessionRecord session, string name, int tailLines, bool previous, CancellationToken ct = default)
    {
        RequireEnabled();
        var ns = SessionContainerSpecFactory.NamespaceName(_options, session.Id);
        var pods = await _cluster.ListPodsAsync(ns, $"{ComponentSelector},{SessionContainerSpecFactory.ContainerLabel}={name}", ct);
        if (pods.Count == 0) throw new KeyNotFoundException($"no container named '{name}'.");
        // 48 KiB: the in-pod MCP client accepts 64 KiB per response, and the JSON escaping of a
        // log's quotes and newlines needs the rest.
        return await _cluster.ReadLogAsync(ns, name, Math.Clamp(tailLines, 1, 2000), 48 * 1024, previous, ct);
    }

    public async Task<bool> StopAsync(SessionRecord session, string name, CancellationToken ct = default)
    {
        RequireEnabled();
        var ns = SessionContainerSpecFactory.NamespaceName(_options, session.Id);
        var pods = await _cluster.ListPodsAsync(ns, $"{ComponentSelector},{SessionContainerSpecFactory.ContainerLabel}={name}", ct);
        if (pods.Count == 0) return false;
        await _cluster.DeleteServiceAsync(ns, name, ct);
        await _cluster.DeletePodAsync(ns, name, ct);
        return true;
    }

    /// <summary>
    /// One namespace delete removes every helper, service, policy and quota of the session; the
    /// session pod's egress policy towards it lives in the sessions namespace and goes separately.
    /// Failures are logged, not thrown: a session delete must not fail over its helpers, and the
    /// reconcile sweep removes what is left.
    /// </summary>
    public async Task CleanupSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_options.Enabled) return;
        try
        {
            await _cluster.DeleteNamespaceAsync(SessionContainerSpecFactory.NamespaceName(_options, sessionId), ct);
            await _cluster.DeleteNetworkPoliciesAsync(_sessionsNamespace,
                $"{SessionContainerSpecFactory.SessionEgressLabel}=true,{SessionContainerSpecFactory.SessionLabel}={sessionId}", ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogWarning(error, "Could not remove the helper containers of session {Session}; the sweep retries", sessionId);
        }
    }

    /// <summary>Session ids of every helper namespace this instance owns.</summary>
    public async Task<IReadOnlyList<string>> ListSessionNamespacesAsync(CancellationToken ct = default)
    {
        var namespaces = await _cluster.ListNamespacesAsync($"{SessionContainerSpecFactory.HubLabel}={_options.HubId}", ct);
        return namespaces
            .Where(n => n.Metadata.DeletionTimestamp is null)
            .Select(n => Label(n.Metadata, SessionContainerSpecFactory.SessionLabel))
            .OfType<string>()
            .ToList();
    }

    /// <returns>Whether helpers run as root, read off the namespace's PSA level.</returns>
    private async Task<bool> EnsureNamespaceAsync(SessionRecord session, CancellationToken ct)
    {
        var name = SessionContainerSpecFactory.NamespaceName(_options, session.Id);
        var existing = await _cluster.GetNamespaceAsync(name, ct);
        if (existing?.Metadata.DeletionTimestamp is not null)
            throw new ContainerRequestException("the containers of this session's previous run are still being removed; try again in a few seconds.");
        if (existing is not null &&
            (Label(existing.Metadata, SessionContainerSpecFactory.HubLabel) != _options.HubId
             || Label(existing.Metadata, SessionContainerSpecFactory.SessionLabel) != session.Id))
            throw new InvalidOperationException($"Namespace {name} exists but does not belong to this session.");

        var resources = SessionContainerSpecFactory.BuildNamespace(_options, _sessionsNamespace, session.Id,
            KubernetesSessionService.OwnerKey(session.Owner), session.RunAsRoot);
        if (existing is null) await _cluster.CreateNamespaceAsync(resources.Namespace, ct);
        await _cluster.CreateRoleBindingAsync(resources.RoleBinding, ct);

        // Every step runs on each call, not only after a fresh create: a backend that died
        // between the namespace and its policies left a namespace without them, and the next
        // run is what completes it. The policies come before any pod so none ever runs unconfined.
        await RetryForbiddenAsync(async () => { await _cluster.CreateQuotaAsync(resources.Quota, ct); return true; }, ct);
        await _cluster.CreateLimitRangeAsync(resources.Limits, ct);
        foreach (var policy in resources.Policies) await _cluster.CreateNetworkPolicyAsync(policy, ct);
        await _cluster.CreateNetworkPolicyAsync(resources.SessionEgress, ct);

        var level = Label((existing ?? resources.Namespace).Metadata, SessionContainerSpecFactory.PsaEnforce);
        return level == "baseline";
    }

    /// <summary>
    /// The backend's rights in a fresh namespace come from the RoleBinding it has just created,
    /// and the API server's authorizer learns about that binding from a watch, a moment later.
    /// The first request after it can therefore be refused; a short retry covers the gap.
    /// </summary>
    private async Task<T> RetryForbiddenAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await action(); }
            catch (HttpOperationException error)
                when (error.Response.StatusCode == System.Net.HttpStatusCode.Forbidden && attempt < 8 && IsRbacDenial(error))
            {
                await _delay(TimeSpan.FromMilliseconds(250 * attempt), ct);
            }
        }
    }

    // An RBAC refusal reads "... is forbidden: User ... cannot create resource ..."; PSA and quota
    // refusals are 403s as well but say "violates PodSecurity" / "exceeded quota", and retrying
    // those only delays the answer.
    private static bool IsRbacDenial(HttpOperationException error) =>
        (error.Response.Content ?? "").Contains("cannot ", StringComparison.Ordinal);

    private static string ApiMessage(HttpOperationException error)
    {
        try
        {
            var status = System.Text.Json.JsonSerializer.Deserialize<V1Status>(error.Response.Content ?? "",
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (!string.IsNullOrWhiteSpace(status?.Message)) return status.Message;
        }
        catch (System.Text.Json.JsonException) { }
        return "forbidden";
    }

    private static string? Label(V1ObjectMeta meta, string key) =>
        meta.Labels is not null && meta.Labels.TryGetValue(key, out var value) ? value : null;

    private void RequireEnabled()
    {
        if (!_options.Enabled) throw new SessionContainersDisabledException();
    }

    private static string ComponentSelector =>
        $"{SessionContainerSpecFactory.ComponentLabel}={SessionContainerSpecFactory.Component}";

    private static ContainerInfo ToInfo(V1Pod pod, string ns)
    {
        var container = pod.Spec?.Containers?.FirstOrDefault();
        var status = pod.Status?.ContainerStatuses?.FirstOrDefault();
        var reason = status?.State?.Waiting?.Reason ?? status?.State?.Terminated?.Reason ?? pod.Status?.Reason;
        var ports = container?.Ports?.Select(p => p.ContainerPort).ToList() ?? [];
        var name = pod.Metadata.Name;
        return new ContainerInfo(
            name, container?.Image ?? "", pod.Status?.Phase ?? "Pending", status?.Ready ?? false,
            status?.RestartCount ?? 0, reason,
            ports.Count > 0 ? SessionContainerSpecFactory.HostName(name, ns) : null,
            ports, pod.Metadata.CreationTimestamp);
    }
}

public sealed class SessionContainersDisabledException : Exception
{
    public SessionContainersDisabledException() : base("Session containers are disabled on this instance.") { }
}
