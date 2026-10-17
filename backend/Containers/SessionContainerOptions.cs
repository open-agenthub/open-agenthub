namespace AgentHub.Api.Containers;

/// <summary>
/// Helper containers a session starts over MCP (bound from the "SessionContainers" section;
/// docs/session-containers.md). Every value here is an upper bound the hub enforces itself; the
/// namespace's ResourceQuota and Pod Security Admission label enforce the same limits a second
/// time, so a bug in the request validation cannot exceed them.
/// </summary>
public sealed class SessionContainerOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// The value of <see cref="SessionContainerSpecFactory.HubLabel"/> on every namespace this
    /// instance creates — the release namespace. The admission policy in the chart admits the
    /// backend's namespace writes only for namespaces carrying it, so a second hub on the same
    /// cluster can neither create into nor delete this one's namespaces.
    /// </summary>
    public string HubId { get; set; } = "agenthub";

    /// <summary>Session namespaces are named <c>{prefix}{sessionId}</c>. The chart's admission
    /// policy checks the same prefix.</summary>
    public string NamespacePrefix { get; set; } = "agenthub-sc-";

    /// <summary>The ClusterRole the backend binds to itself inside each session namespace.</summary>
    public string ClusterRoleName { get; set; } = "agenthub-session-containers";

    /// <summary>The backend's own service account, the only subject of that binding.</summary>
    public string BackendServiceAccount { get; set; } = "agenthub-backend";

    /// <summary>
    /// Comma-separated registry hosts a helper image may come from, e.g. "docker.io, ghcr.io".
    /// Empty = any registry, which is what a custom session image may use today. A single string
    /// for the same reason as Network:RequestablePorts: clearing the list in the chart reliably
    /// yields an empty value instead of the default.
    /// </summary>
    public string AllowedRegistries { get; set; } = "";

    /// <summary>Only <c>image@sha256:…</c> references are accepted.</summary>
    public bool RequireDigest { get; set; }

    /// <summary>Comma-separated TCP egress ports of a helper — the session pod's own list
    /// (22, 80, 443 plus agent.extraEgressPorts); DNS is always added.</summary>
    public string EgressPorts { get; set; } = "22, 80, 443";

    // ResourceQuota of one session namespace.
    public int MaxPods { get; set; } = 5;
    public string QuotaCpu { get; set; } = "2";
    public string QuotaMemory { get; set; } = "4Gi";
    public string QuotaEphemeralStorage { get; set; } = "10Gi";

    // Per helper container.
    public string DefaultCpu { get; set; } = "250m";
    public string DefaultMemory { get; set; } = "256Mi";
    public string MaxCpu { get; set; } = "1";
    public string MaxMemory { get; set; } = "2Gi";
    /// <summary>Size limit of one emptyDir volume, and the ephemeral-storage limit of a helper.</summary>
    public string MaxVolumeSize { get; set; } = "2Gi";
}
