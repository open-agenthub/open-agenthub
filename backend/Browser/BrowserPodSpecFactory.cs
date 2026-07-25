using System.Net;
using AgentHub.Api.Persistence;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Browser;

public sealed record BrowserPodResources(
    V1Pod Pod,
    V1NetworkPolicy CdpIngress,
    V1NetworkPolicy CdpEgress,
    V1NetworkPolicy BrowserEgress,
    V1NetworkPolicy VncIngress);

public sealed record BrowserPodContext
{
    public required string Namespace { get; init; }
    public required string ControlNamespace { get; init; }
    public required string CallbackUrl { get; init; }
    public required string LeaseToken { get; init; }
    public required IPAddress AgentPodIp { get; init; }
    public required BrowserOptions Options { get; init; }
}

public static class BrowserPodSpecFactory
{
    private const string SessionLabel = "agenthub.dev/session";
    private const string ComponentLabel = "agenthub.dev/component";
    public const string LeaseLabel = "agenthub.dev/browser-lease";

    public static BrowserPodResources Build(
        SessionRecord session, BrowserLease lease, BrowserPodContext context)
    {
        var options = context.Options;
        var name = $"browser-{session.Id}";
        var labels = Labels(session.Id, "browser");
        labels[LeaseLabel] = lease.LeaseId;
        var security = new V1SecurityContext
        {
            AllowPrivilegeEscalation = false,
            ReadOnlyRootFilesystem = true,
            RunAsNonRoot = true,
            RunAsUser = 1000,
            RunAsGroup = 1000,
            Capabilities = new V1Capabilities { Drop = ["ALL"] }
        };
        var mounts = new List<V1VolumeMount>
        {
            new() { Name = "data", MountPath = "/data" },
            new() { Name = "tmp", MountPath = "/tmp" },
            new() { Name = "shm", MountPath = "/dev/shm" }
        };
        var container = new V1Container
        {
            Name = "browser",
            Image = options.Image,
            ImagePullPolicy = options.PullPolicy,
            SecurityContext = security,
            Env =
            [
                new() { Name = "HOME", Value = "/data/home" },
                new() { Name = "AGENTHUB_BROWSER_SCREEN", Value = $"{options.ScreenWidth}x{options.ScreenHeight}" },
                new() { Name = "AGENTHUB_BROWSER_LEASE_ID", Value = lease.LeaseId },
                new() { Name = "AGENTHUB_BROWSER_LEASE_TOKEN", Value = context.LeaseToken },
                new() { Name = "AGENTHUB_BROWSER_CALLBACK_URL", Value = context.CallbackUrl },
                new() { Name = "AGENTHUB_BROWSER_COOKIE_CHECKPOINT_SECONDS", Value = options.CookieCheckpointSeconds.ToString() },
                new() { Name = "AGENTHUB_BROWSER_COOKIE_MAX_BYTES", Value = options.CookieStateMaxBytes.ToString() }
            ],
            Ports =
            [
                new() { Name = "cdp", ContainerPort = 9222 },
                new() { Name = "rfb-ws", ContainerPort = 6080 },
                new() { Name = "rfb-view", ContainerPort = 6082 },
                new() { Name = "health", ContainerPort = 6081 }
            ],
            VolumeMounts = mounts,
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new(options.CpuRequest), ["memory"] = new(options.MemoryRequest)
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new(options.CpuLimit), ["memory"] = new(options.MemoryLimit)
                }
            },
            ReadinessProbe = new V1Probe
            {
                HttpGet = new V1HTTPGetAction { Path = "/healthz", Port = 6081 },
                InitialDelaySeconds = 2,
                PeriodSeconds = 2,
                TimeoutSeconds = 1,
                FailureThreshold = 45
            }
        };
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta
            {
                Name = name,
                NamespaceProperty = context.Namespace,
                Labels = labels
            },
            Spec = new V1PodSpec
            {
                AutomountServiceAccountToken = false,
                ServiceAccountName = "agenthub-agent",
                EnableServiceLinks = false,
                RestartPolicy = "Never",
                TerminationGracePeriodSeconds = 50,
                RuntimeClassName = string.IsNullOrWhiteSpace(options.RuntimeClassName)
                    ? null : options.RuntimeClassName,
                SecurityContext = new V1PodSecurityContext
                {
                    RunAsNonRoot = true,
                    RunAsUser = 1000,
                    RunAsGroup = 1000,
                    FsGroup = 1000,
                    SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
                },
                Containers = [container],
                Volumes =
                [
                    new() { Name = "data", EmptyDir = new V1EmptyDirVolumeSource() },
                    new() { Name = "tmp", EmptyDir = new V1EmptyDirVolumeSource() },
                    new() { Name = "shm", EmptyDir = new V1EmptyDirVolumeSource { Medium = "Memory", SizeLimit = new ResourceQuantity("512Mi") } }
                ],
                ImagePullSecrets = string.IsNullOrWhiteSpace(options.ImagePullSecret)
                    ? null : [new V1LocalObjectReference { Name = options.ImagePullSecret }]
            }
        };

        var cdpPort = new V1NetworkPolicyPort(protocol: "TCP", port: 9222);
        var cdpIngress = Policy(
            $"{name}-cdp-in", context.Namespace, Labels(session.Id, "browser"), ["Ingress"], lease.LeaseId,
            ingress:
            [
                new V1NetworkPolicyIngressRule
                {
                    FromProperty = [new V1NetworkPolicyPeer(ipBlock: new V1IPBlock
                    {
                        Cidr = $"{context.AgentPodIp}/{(context.AgentPodIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)}"
                    })],
                    Ports = [cdpPort]
                }
            ]);
        var cdpEgress = Policy(
            $"{name}-cdp-out", context.Namespace, Labels(session.Id, "agent"), ["Egress"], lease.LeaseId,
            egress:
            [
                new V1NetworkPolicyEgressRule
                {
                    To = [new V1NetworkPolicyPeer(podSelector: Selector(Labels(session.Id, "browser")))],
                    Ports = [cdpPort]
                }
            ]);
        var backendPeer = new V1NetworkPolicyPeer(
            namespaceSelector: Selector(new Dictionary<string, string>
            {
                ["kubernetes.io/metadata.name"] = context.ControlNamespace
            }),
            podSelector: Selector(new Dictionary<string, string> { ["app"] = "agenthub-backend" }));
        var internetPeer = new V1NetworkPolicyPeer
        {
            IpBlock = new V1IPBlock { Cidr = "0.0.0.0/0" }
        };
        var webPorts = new[] { 80, 443 }
            .Concat(options.ExtraEgressPorts)
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .Select(port => new V1NetworkPolicyPort(protocol: "TCP", port: port))
            .ToList();
        var browserEgress = Policy(
            $"{name}-egress", context.Namespace, Labels(session.Id, "browser"), ["Egress"], lease.LeaseId,
            egress:
            [
                new V1NetworkPolicyEgressRule
                {
                    To = [internetPeer],
                    Ports =
                    [
                        new V1NetworkPolicyPort(protocol: "UDP", port: 53),
                        new V1NetworkPolicyPort(protocol: "TCP", port: 53)
                    ]
                },
                new V1NetworkPolicyEgressRule { To = [internetPeer], Ports = webPorts },
                new V1NetworkPolicyEgressRule
                {
                    To = [backendPeer],
                    Ports =
                    [
                        new V1NetworkPolicyPort(protocol: "TCP", port: 80),
                        new V1NetworkPolicyPort(protocol: "TCP", port: 8080)
                    ]
                }
            ]);

        var vncIngress = Policy(
            $"{name}-vnc-in", context.Namespace, Labels(session.Id, "browser"), ["Ingress"], lease.LeaseId,
            ingress:
            [
                new V1NetworkPolicyIngressRule
                {
                    FromProperty = [backendPeer],
                    Ports =
                    [
                        new V1NetworkPolicyPort(protocol: "TCP", port: 6080),
                        new V1NetworkPolicyPort(protocol: "TCP", port: 6082)
                    ]
                }
            ]);

        return new BrowserPodResources(pod, cdpIngress, cdpEgress, browserEgress, vncIngress);
    }

    private static V1NetworkPolicy Policy(string name, string ns,
        Dictionary<string, string> selector, IList<string> policyTypes, string leaseId,
        IList<V1NetworkPolicyIngressRule>? ingress = null,
        IList<V1NetworkPolicyEgressRule>? egress = null) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = ns,
            Labels = new Dictionary<string, string>
            {
                [SessionLabel] = selector[SessionLabel],
                ["agenthub.dev/browser-resource"] = "true",
                [LeaseLabel] = leaseId
            }
        },
        Spec = new V1NetworkPolicySpec
        {
            PodSelector = Selector(selector),
            PolicyTypes = policyTypes,
            Ingress = ingress,
            Egress = egress
        }
    };

    private static V1LabelSelector Selector(Dictionary<string, string> labels) =>
        new(matchLabels: labels);


    private static Dictionary<string, string> Labels(string sessionId, string component) => new()
    {
        [SessionLabel] = sessionId,
        [ComponentLabel] = component
    };
}
