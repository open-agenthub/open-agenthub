namespace AgentHub.Api.Containers;

/// <summary>
/// What an agent may ask for. Deliberately small: every field of a pod that decides privilege —
/// security context, service account, host namespaces, volumes other than emptyDir, node
/// placement — is chosen by the hub and cannot be expressed here (docs/session-containers.md).
/// </summary>
public sealed record ContainerRunRequest
{
    public string Name { get; init; } = "";
    public string Image { get; init; } = "";
    public IReadOnlyList<string>? Command { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public IReadOnlyList<int>? Ports { get; init; }
    public string? Cpu { get; init; }
    public string? Memory { get; init; }
    public IReadOnlyList<ContainerVolumeRequest>? Volumes { get; init; }
    /// <summary>Many server images write next to their binaries (pid files, sockets). A writable
    /// root filesystem is the container's own overlay layer, bounded by its ephemeral-storage
    /// limit, so allowing it grants no access the session does not have.</summary>
    public bool WritableRootFilesystem { get; init; }
}

public sealed record ContainerVolumeRequest(string MountPath, string? SizeLimit = null);

public sealed record ContainerInfo(
    string Name,
    string Image,
    string Phase,
    bool Ready,
    int Restarts,
    string? Reason,
    string? Host,
    IReadOnlyList<int> Ports,
    DateTime? CreatedAt);

/// <summary>A request the hub refuses to turn into a pod; the message goes back to the agent.</summary>
public sealed class ContainerRequestException(string message) : Exception(message);
