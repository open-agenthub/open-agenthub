using System.Text.RegularExpressions;
using AgentHub.Api.Services;
using k8s.Models;

namespace AgentHub.Api.Containers;

/// <summary>Everything that has to exist in a session namespace before its first helper pod.</summary>
public sealed record SessionNamespaceResources(
    V1Namespace Namespace,
    V1RoleBinding RoleBinding,
    V1ResourceQuota Quota,
    V1LimitRange Limits,
    IReadOnlyList<V1NetworkPolicy> Policies,
    V1NetworkPolicy SessionEgress);

/// <summary>
/// Pure construction of a session's helper namespace and the pods in it
/// (docs/session-containers.md). Kept apart from the cluster calls so the properties that make
/// the feature safe — the security context, the PSA label, the quota and the policies — are
/// asserted by unit tests rather than by reading a running cluster.
/// </summary>
public static partial class SessionContainerSpecFactory
{
    public const string HubLabel = "agenthub.dev/session-containers";
    public const string SessionLabel = "agenthub.dev/session";
    public const string OwnerLabel = "agenthub.dev/owner";
    public const string ComponentLabel = "agenthub.dev/component";
    public const string ContainerLabel = "agenthub.dev/container";
    public const string Component = "session-container";
    /// <summary>Marks the session pod's egress policy towards its helper namespace, so session
    /// teardown removes it by selector like the port-request policies.</summary>
    public const string SessionEgressLabel = "agenthub.dev/session-containers-egress";
    public const string PsaEnforce = "pod-security.kubernetes.io/enforce";
    public const int MaxEnv = 64;
    public const int MaxArgs = 64;
    public const int MaxPorts = 8;
    public const int MaxVolumes = 4;

    public static string NamespaceName(SessionContainerOptions options, string sessionId) =>
        $"{options.NamespacePrefix}{sessionId}";

    /// <summary>
    /// <c>restricted</c> for every session, <c>baseline</c> for a session that itself runs as root.
    /// Never <c>privileged</c>: the admission policy in the chart refuses a namespace with any
    /// other value, so even a bug here cannot open the namespace to privileged pods.
    /// </summary>
    public static string PodSecurityLevel(bool sessionRunsAsRoot) =>
        sessionRunsAsRoot ? "baseline" : "restricted";

    public static SessionNamespaceResources BuildNamespace(
        SessionContainerOptions options, string sessionsNamespace, string sessionId, string ownerKey,
        bool sessionRunsAsRoot)
    {
        var ns = NamespaceName(options, sessionId);
        var level = PodSecurityLevel(sessionRunsAsRoot);
        var labels = new Dictionary<string, string>
        {
            [HubLabel] = options.HubId,
            [SessionLabel] = sessionId,
            [OwnerLabel] = ownerKey,
            [PsaEnforce] = level,
            ["pod-security.kubernetes.io/enforce-version"] = "latest",
            ["pod-security.kubernetes.io/warn"] = level,
            ["pod-security.kubernetes.io/audit"] = level
        };
        var meta = (string name) => new V1ObjectMeta
        {
            Name = name, NamespaceProperty = ns,
            Labels = new Dictionary<string, string> { [HubLabel] = options.HubId, [SessionLabel] = sessionId }
        };

        var quota = new V1ResourceQuota
        {
            Metadata = meta("session-containers"),
            Spec = new V1ResourceQuotaSpec
            {
                Hard = new Dictionary<string, ResourceQuantity>
                {
                    ["pods"] = new(options.MaxPods.ToString()),
                    ["services"] = new(options.MaxPods.ToString()),
                    ["services.nodeports"] = new("0"),
                    ["services.loadbalancers"] = new("0"),
                    // emptyDir only for now; a claim would outlive the pod's own storage limit.
                    ["persistentvolumeclaims"] = new("0"),
                    ["requests.cpu"] = new(options.QuotaCpu),
                    ["limits.cpu"] = new(options.QuotaCpu),
                    ["requests.memory"] = new(options.QuotaMemory),
                    ["limits.memory"] = new(options.QuotaMemory),
                    ["requests.ephemeral-storage"] = new(options.QuotaEphemeralStorage),
                    ["limits.ephemeral-storage"] = new(options.QuotaEphemeralStorage)
                }
            }
        };

        var limits = new V1LimitRange
        {
            Metadata = meta("session-containers"),
            Spec = new V1LimitRangeSpec
            {
                Limits = new List<V1LimitRangeItem>
                {
                    new()
                    {
                        Type = "Container",
                        Max = new Dictionary<string, ResourceQuantity>
                        {
                            ["cpu"] = new(options.MaxCpu), ["memory"] = new(options.MaxMemory),
                            ["ephemeral-storage"] = new(options.MaxVolumeSize)
                        },
                        DefaultProperty = new Dictionary<string, ResourceQuantity>
                        {
                            ["cpu"] = new(options.DefaultCpu), ["memory"] = new(options.DefaultMemory),
                            ["ephemeral-storage"] = new(options.MaxVolumeSize)
                        },
                        DefaultRequest = new Dictionary<string, ResourceQuantity>
                        {
                            ["cpu"] = new(options.DefaultCpu), ["memory"] = new(options.DefaultMemory),
                            ["ephemeral-storage"] = new(options.MaxVolumeSize)
                        }
                    }
                }
            }
        };

        var roleBinding = new V1RoleBinding
        {
            Metadata = meta("agenthub-backend"),
            RoleRef = new V1RoleRef { ApiGroup = "rbac.authorization.k8s.io", Kind = "ClusterRole", Name = options.ClusterRoleName },
            Subjects = new List<Rbacv1Subject>
            {
                new() { Kind = "ServiceAccount", Name = options.BackendServiceAccount, NamespaceProperty = options.HubId }
            }
        };

        var everyPod = new V1LabelSelector();
        var sessionPod = new V1NetworkPolicyPeer
        {
            NamespaceSelector = NameSelector(sessionsNamespace),
            PodSelector = new V1LabelSelector(matchLabels: new Dictionary<string, string>
            {
                [SessionLabel] = sessionId, [ComponentLabel] = "agent"
            })
        };
        var sameNamespace = new V1NetworkPolicyPeer { PodSelector = new V1LabelSelector() };
        var egressPorts = ParsePorts(options.EgressPorts)
            .Select(port => new V1NetworkPolicyPort(protocol: "TCP", port: port))
            .ToList();
        var egressRules = new List<V1NetworkPolicyEgressRule>
        {
            new() { Ports = [new V1NetworkPolicyPort(protocol: "UDP", port: 53), new V1NetworkPolicyPort(protocol: "TCP", port: 53)] },
            // Helpers of one session talk to each other (a service under test and its database).
            new() { To = [sameNamespace] }
        };
        // No `to` on purpose: the session pod's own allow-agent-egress has none either, so a
        // helper reaches exactly what its session reaches — and, like the session pod, not the
        // backend's port 8080, which only allow-agent-to-backend opens and only for agent pods.
        if (egressPorts.Count > 0) egressRules.Add(new V1NetworkPolicyEgressRule { Ports = egressPorts });

        var policies = new List<V1NetworkPolicy>
        {
            new()
            {
                Metadata = meta("default-deny"),
                Spec = new V1NetworkPolicySpec { PodSelector = everyPod, PolicyTypes = ["Ingress", "Egress"] }
            },
            new()
            {
                Metadata = meta("allow-from-session"),
                Spec = new V1NetworkPolicySpec
                {
                    PodSelector = everyPod, PolicyTypes = ["Ingress"],
                    Ingress = [new V1NetworkPolicyIngressRule { FromProperty = [sessionPod, sameNamespace] }]
                }
            },
            new()
            {
                Metadata = meta("allow-egress"),
                Spec = new V1NetworkPolicySpec { PodSelector = everyPod, PolicyTypes = ["Egress"], Egress = egressRules }
            }
        };

        // The sessions namespace is default-deny, so without this the agent could start a
        // database and never connect to it.
        var sessionEgress = new V1NetworkPolicy
        {
            Metadata = new V1ObjectMeta
            {
                Name = $"session-{sessionId}-containers-out",
                NamespaceProperty = sessionsNamespace,
                Labels = new Dictionary<string, string> { [SessionLabel] = sessionId, [SessionEgressLabel] = "true" }
            },
            Spec = new V1NetworkPolicySpec
            {
                PodSelector = new V1LabelSelector(matchLabels: new Dictionary<string, string>
                {
                    [SessionLabel] = sessionId, [ComponentLabel] = "agent"
                }),
                PolicyTypes = ["Egress"],
                Egress = [new V1NetworkPolicyEgressRule { To = [new V1NetworkPolicyPeer { NamespaceSelector = NameSelector(ns) }] }]
            }
        };

        return new SessionNamespaceResources(
            new V1Namespace { Metadata = new V1ObjectMeta { Name = ns, Labels = labels } },
            roleBinding, quota, limits, policies, sessionEgress);
    }

    /// <summary>
    /// Checks a request against the instance limits. Throws <see cref="ContainerRequestException"/>
    /// with a message meant for the agent; everything that passes is safe to build.
    /// </summary>
    public static void Validate(ContainerRunRequest request, SessionContainerOptions options)
    {
        if (!NamePattern().IsMatch(request.Name ?? ""))
            throw new ContainerRequestException(
                "name must be 1-40 lowercase letters, digits or '-', starting with a letter; it becomes the DNS name.");
        ValidateImage(request.Image, options);

        if (request.Command is { Count: > MaxArgs } || request.Args is { Count: > MaxArgs })
            throw new ContainerRequestException($"command and args take at most {MaxArgs} entries each.");
        if ((request.Command ?? []).Concat(request.Args ?? []).Any(a => a is null || a.Length > 4096))
            throw new ContainerRequestException("command and args entries must be at most 4096 characters.");

        if (request.Env is { Count: > MaxEnv })
            throw new ContainerRequestException($"env takes at most {MaxEnv} variables.");
        foreach (var (key, value) in request.Env ?? new Dictionary<string, string>())
        {
            if (!EnvNamePattern().IsMatch(key)) throw new ContainerRequestException($"invalid env name '{key}'.");
            if (value is null || value.Length > 32_768) throw new ContainerRequestException($"env {key} is too long.");
        }

        var ports = request.Ports ?? [];
        if (ports.Count > MaxPorts) throw new ContainerRequestException($"at most {MaxPorts} ports.");
        if (ports.Any(p => p is < 1 or > 65535) || ports.Distinct().Count() != ports.Count)
            throw new ContainerRequestException("ports must be distinct numbers between 1 and 65535.");

        CheckQuantity(request.Cpu, options.MaxCpu, "cpu");
        CheckQuantity(request.Memory, options.MaxMemory, "memory");

        var volumes = request.Volumes ?? [];
        if (volumes.Count > MaxVolumes) throw new ContainerRequestException($"at most {MaxVolumes} volumes.");
        var paths = new HashSet<string>(StringComparer.Ordinal) { "/tmp" };
        foreach (var volume in volumes)
        {
            var path = volume.MountPath ?? "";
            if (!MountPathPattern().IsMatch(path) || path.Split('/').Any(s => s is "." or "..")
                || ReservedMountRoots.Any(root => path == root || path.StartsWith(root + "/", StringComparison.Ordinal)))
                throw new ContainerRequestException($"invalid volume mountPath '{path}'.");
            if (!paths.Add(path.TrimEnd('/')))
                throw new ContainerRequestException($"mountPath '{path}' is used twice (/tmp is always mounted).");
            CheckQuantity(volume.SizeLimit, options.MaxVolumeSize, "volume sizeLimit");
        }
    }

    public static void ValidateImage(string? image, SessionContainerOptions options)
    {
        // The same reference syntax a custom session image is held to (KubernetesSessionService).
        if (string.IsNullOrWhiteSpace(image) || image.Length > 300 || !ImagePattern().IsMatch(image))
            throw new ContainerRequestException("invalid image reference.");
        if (options.RequireDigest && !DigestPattern().IsMatch(image))
            throw new ContainerRequestException("this instance only accepts images pinned by digest (image@sha256:...).");
        var allowed = ParseList(options.AllowedRegistries).Select(NormalizeRegistry).ToHashSet(StringComparer.Ordinal);
        if (allowed.Count > 0 && !allowed.Contains(RegistryOf(image)))
            throw new ContainerRequestException(
                $"registry '{RegistryOf(image)}' is not allowed on this instance; allowed: {string.Join(", ", allowed.Order())}.");
    }

    /// <summary>
    /// The registry host of an image reference, by the same rule a container runtime resolves
    /// it: the first path segment is a host only when it looks like one, otherwise the image is
    /// on the default registry. Comparing the raw prefix instead let "docker.io.example.com/x"
    /// pass a "docker.io" allow list.
    /// </summary>
    public static string RegistryOf(string image)
    {
        var slash = image.IndexOf('/');
        if (slash < 0) return "docker.io";
        var first = image[..slash];
        return first.Contains('.') || first.Contains(':') || first == "localhost"
            ? NormalizeRegistry(first)
            : "docker.io";
    }

    private static string NormalizeRegistry(string host)
    {
        host = host.Trim().ToLowerInvariant();
        return host is "index.docker.io" or "registry-1.docker.io" ? "docker.io" : host;
    }

    /// <param name="sessionRunsAsRoot">Read off the namespace's PSA label, not the session
    /// record: a session switched to root takes effect at its next start, and until then its
    /// namespace — and so its helpers — stay restricted.</param>
    public static (V1Pod Pod, V1Service? Service) BuildPod(
        SessionContainerOptions options, AgentPodRuntimeSettings runtime, string sessionId,
        bool sessionRunsAsRoot, ContainerRunRequest request)
    {
        Validate(request, options);
        var ns = NamespaceName(options, sessionId);
        var labels = new Dictionary<string, string>
        {
            [SessionLabel] = sessionId, [ComponentLabel] = Component, [ContainerLabel] = request.Name
        };
        var cpu = string.IsNullOrWhiteSpace(request.Cpu) ? options.DefaultCpu : request.Cpu;
        var memory = string.IsNullOrWhiteSpace(request.Memory) ? options.DefaultMemory : request.Memory;
        // Requests equal limits: the quota is then a hard sum, not something a burst can exceed.
        var resources = new Dictionary<string, ResourceQuantity>
        {
            ["cpu"] = new(cpu), ["memory"] = new(memory), ["ephemeral-storage"] = new(options.MaxVolumeSize)
        };

        var security = AgentPodSpecFactory.ContainerSecurity(sessionRunsAsRoot);
        if (request.WritableRootFilesystem) security.ReadOnlyRootFilesystem = false;

        // /tmp is always writable, as in the session pod: most images need it with a read-only root.
        var volumes = new List<V1Volume>
        {
            new() { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity(options.MaxVolumeSize) } }
        };
        var mounts = new List<V1VolumeMount> { new() { Name = "tmp", MountPath = "/tmp" } };
        var index = 0;
        foreach (var volume in request.Volumes ?? [])
        {
            var name = $"data-{index++}";
            var size = string.IsNullOrWhiteSpace(volume.SizeLimit) ? options.MaxVolumeSize : volume.SizeLimit;
            volumes.Add(new V1Volume { Name = name, EmptyDir = new V1EmptyDirVolumeSource { SizeLimit = new ResourceQuantity(size) } });
            mounts.Add(new V1VolumeMount { Name = name, MountPath = volume.MountPath });
        }

        var ports = request.Ports ?? [];
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = request.Name, NamespaceProperty = ns, Labels = labels },
            Spec = new V1PodSpec
            {
                RestartPolicy = "Always",
                AutomountServiceAccountToken = false,
                EnableServiceLinks = false,
                SecurityContext = AgentPodSpecFactory.PodSecurity(sessionRunsAsRoot),
                RuntimeClassName = string.IsNullOrEmpty(runtime.RuntimeClassName) ? null : runtime.RuntimeClassName,
                Containers = new List<V1Container>
                {
                    new()
                    {
                        Name = "main",
                        Image = request.Image,
                        ImagePullPolicy = "IfNotPresent",
                        Command = request.Command is { Count: > 0 } ? request.Command.ToList() : null,
                        Args = request.Args is { Count: > 0 } ? request.Args.ToList() : null,
                        Env = request.Env?.Select(e => new V1EnvVar(e.Key, e.Value)).ToList(),
                        Ports = ports.Count > 0
                            ? ports.Select(p => new V1ContainerPort { ContainerPort = p, Protocol = "TCP" }).ToList()
                            : null,
                        SecurityContext = security,
                        Resources = new V1ResourceRequirements { Requests = resources, Limits = new Dictionary<string, ResourceQuantity>(resources) },
                        VolumeMounts = mounts
                    }
                },
                Volumes = volumes
            }
        };

        V1Service? service = null;
        if (ports.Count > 0)
        {
            service = new V1Service
            {
                Metadata = new V1ObjectMeta { Name = request.Name, NamespaceProperty = ns, Labels = labels },
                Spec = new V1ServiceSpec
                {
                    Type = "ClusterIP",
                    Selector = new Dictionary<string, string> { [SessionLabel] = sessionId, [ContainerLabel] = request.Name },
                    Ports = ports.Select(p => new V1ServicePort { Name = $"tcp-{p}", Port = p, TargetPort = p, Protocol = "TCP" }).ToList()
                }
            };
        }
        return (pod, service);
    }

    /// <summary>The name the session pod resolves: its own search path is the sessions namespace,
    /// so the helper's namespace has to be part of the name.</summary>
    public static string HostName(string name, string namespaceName) => $"{name}.{namespaceName}.svc";

    public static IReadOnlyList<int> ParsePorts(string? setting) =>
        ParseList(setting)
            .Select(p => int.TryParse(p, out var port) && port is >= 1 and <= 65535 ? port : 0)
            .Where(p => p > 0)
            .Distinct()
            .ToList();

    private static IEnumerable<string> ParseList(string? setting) =>
        (setting ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void CheckQuantity(string? value, string max, string what)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        decimal requested;
        try { requested = new ResourceQuantity(value).ToDecimal(); }
        catch (Exception) { throw new ContainerRequestException($"invalid {what} '{value}'."); }
        if (requested <= 0 || requested > new ResourceQuantity(max).ToDecimal())
            throw new ContainerRequestException($"{what} must be more than 0 and at most {max}.");
    }

    private static V1LabelSelector NameSelector(string namespaceName) =>
        new(matchLabels: new Dictionary<string, string> { ["kubernetes.io/metadata.name"] = namespaceName });

    // Kernel and device trees: an emptyDir over them breaks the container in ways that look
    // like an image bug.
    private static readonly string[] ReservedMountRoots = ["/proc", "/sys", "/dev"];

    [GeneratedRegex("^[a-z]([-a-z0-9]{0,38}[a-z0-9])?$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex EnvNamePattern();

    [GeneratedRegex("^/[A-Za-z0-9._/-]{1,200}$")]
    private static partial Regex MountPathPattern();

    [GeneratedRegex("^[A-Za-z0-9._/:@-]+$")]
    private static partial Regex ImagePattern();

    [GeneratedRegex("@sha256:[a-f0-9]{64}$")]
    private static partial Regex DigestPattern();
}
