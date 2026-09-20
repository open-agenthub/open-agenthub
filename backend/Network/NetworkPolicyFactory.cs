using k8s.Models;

namespace AgentHub.Api.Network;

/// <summary>
/// Pure construction of the NetworkPolicies for an approved port request. The session
/// namespace is default-deny, so every grant is additive and scoped to this session's
/// pods via label selectors:
///   - Egress: the agent pod may open the port anywhere (mirrors the static
///     allow-agent-egress policy, one port at a time).
///   - BrowserToAgent: a matching pair — ingress on the agent pod, egress on the
///     browser pod — exactly like the preview-port pattern.
/// All policies carry the session label plus a dedicated resource label so cleanup
/// can delete them as a collection when the session ends.
/// </summary>
public static class NetworkPolicyFactory
{
    private const string SessionLabel = "agenthub.dev/session";
    private const string ComponentLabel = "agenthub.dev/component";
    public const string ResourceLabel = "agenthub.dev/network-request";
    public const string ResourceSelector = ResourceLabel + "=true";

    public static IReadOnlyList<V1NetworkPolicy> Build(
        string sessionId, string namespaceName, PortDirection direction, int port, string protocol)
    {
        protocol = protocol.ToUpperInvariant();
        var policyPort = new V1NetworkPolicyPort(protocol: protocol, port: port);
        var slug = $"{PortRequestKey.WireName(direction).Replace('_', '-')}-{protocol.ToLowerInvariant()}-{port}";
        var prefix = $"session-{sessionId}-net-{slug}";

        if (direction == PortDirection.Egress)
        {
            var anywhere = new V1NetworkPolicyPeer { IpBlock = new V1IPBlock { Cidr = "0.0.0.0/0" } };
            return
            [
                Policy($"{prefix}-out", namespaceName, sessionId, Selector(sessionId, "agent"), ["Egress"],
                    egress: [new V1NetworkPolicyEgressRule { To = [anywhere], Ports = [policyPort] }])
            ];
        }

        var agentPeer = new V1NetworkPolicyPeer(podSelector: Selector(sessionId, "agent"));
        var browserPeer = new V1NetworkPolicyPeer(podSelector: Selector(sessionId, "browser"));
        return
        [
            Policy($"{prefix}-in", namespaceName, sessionId, Selector(sessionId, "agent"), ["Ingress"],
                ingress: [new V1NetworkPolicyIngressRule { FromProperty = [browserPeer], Ports = [policyPort] }]),
            Policy($"{prefix}-out", namespaceName, sessionId, Selector(sessionId, "browser"), ["Egress"],
                egress: [new V1NetworkPolicyEgressRule { To = [agentPeer], Ports = [policyPort] }])
        ];
    }

    private static V1NetworkPolicy Policy(string name, string ns, string sessionId,
        V1LabelSelector podSelector, IList<string> policyTypes,
        IList<V1NetworkPolicyIngressRule>? ingress = null,
        IList<V1NetworkPolicyEgressRule>? egress = null) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = ns,
            Labels = new Dictionary<string, string>
            {
                [SessionLabel] = sessionId,
                [ResourceLabel] = "true"
            }
        },
        Spec = new V1NetworkPolicySpec
        {
            PodSelector = podSelector,
            PolicyTypes = policyTypes,
            Ingress = ingress,
            Egress = egress
        }
    };

    private static V1LabelSelector Selector(string sessionId, string component) =>
        new(matchLabels: new Dictionary<string, string>
        {
            [SessionLabel] = sessionId,
            [ComponentLabel] = component
        });
}
