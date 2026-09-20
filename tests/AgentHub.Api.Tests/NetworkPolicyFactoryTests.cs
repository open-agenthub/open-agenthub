using AgentHub.Api.Network;
using k8s.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public class NetworkPolicyFactoryTests
{
    private const string SessionId = "abc123def456";
    private const string Namespace = "agenthub-sessions";

    [Fact]
    public void Egress_BuildsASingleAgentPodEgressPolicy()
    {
        var policies = NetworkPolicyFactory.Build(SessionId, Namespace, PortDirection.Egress, 5432, "TCP");

        var policy = Assert.Single(policies);
        Assert.Equal($"session-{SessionId}-net-egress-tcp-5432-out", policy.Metadata.Name);
        Assert.Equal(Namespace, policy.Metadata.NamespaceProperty);
        AssertLabels(policy.Metadata);

        Assert.Equal(["Egress"], policy.Spec.PolicyTypes);
        AssertSelector(policy.Spec.PodSelector, "agent");
        Assert.Null(policy.Spec.Ingress);

        var rule = Assert.Single(policy.Spec.Egress);
        var peer = Assert.Single(rule.To);
        Assert.Equal("0.0.0.0/0", peer.IpBlock?.Cidr);
        var port = Assert.Single(rule.Ports);
        Assert.Equal("TCP", port.Protocol);
        Assert.Equal("5432", port.Port.Value);
    }

    [Fact]
    public void Egress_HonorsUdpAndNormalizesProtocolCase()
    {
        var policies = NetworkPolicyFactory.Build(SessionId, Namespace, PortDirection.Egress, 53, "udp");

        var policy = Assert.Single(policies);
        Assert.Equal($"session-{SessionId}-net-egress-udp-53-out", policy.Metadata.Name);
        Assert.Equal("UDP", Assert.Single(Assert.Single(policy.Spec.Egress).Ports).Protocol);
    }

    [Fact]
    public void BrowserToAgent_BuildsAMatchingIngressEgressPair()
    {
        var policies = NetworkPolicyFactory.Build(SessionId, Namespace, PortDirection.BrowserToAgent, 3000, "TCP");

        Assert.Equal(2, policies.Count);
        var ingress = policies[0];
        var egress = policies[1];

        // Ingress on the agent pod, admitting only this session's browser pod.
        Assert.Equal($"session-{SessionId}-net-browser-to-agent-tcp-3000-in", ingress.Metadata.Name);
        AssertLabels(ingress.Metadata);
        Assert.Equal(["Ingress"], ingress.Spec.PolicyTypes);
        AssertSelector(ingress.Spec.PodSelector, "agent");
        var ingressRule = Assert.Single(ingress.Spec.Ingress);
        AssertSelector(Assert.Single(ingressRule.FromProperty).PodSelector, "browser");
        var ingressPort = Assert.Single(ingressRule.Ports);
        Assert.Equal("TCP", ingressPort.Protocol);
        Assert.Equal("3000", ingressPort.Port.Value);

        // Egress on the browser pod, targeting only this session's agent pod.
        Assert.Equal($"session-{SessionId}-net-browser-to-agent-tcp-3000-out", egress.Metadata.Name);
        AssertLabels(egress.Metadata);
        Assert.Equal(["Egress"], egress.Spec.PolicyTypes);
        AssertSelector(egress.Spec.PodSelector, "browser");
        var egressRule = Assert.Single(egress.Spec.Egress);
        AssertSelector(Assert.Single(egressRule.To).PodSelector, "agent");
        var egressPort = Assert.Single(egressRule.Ports);
        Assert.Equal("TCP", egressPort.Protocol);
        Assert.Equal("3000", egressPort.Port.Value);
    }

    [Fact]
    public void Names_AreDeterministic_SoReapplyingAGrantConflictsInsteadOfDuplicating()
    {
        var first = NetworkPolicyFactory.Build(SessionId, Namespace, PortDirection.Egress, 5432, "TCP");
        var second = NetworkPolicyFactory.Build(SessionId, Namespace, PortDirection.Egress, 5432, "TCP");
        Assert.Equal(first.Select(p => p.Metadata.Name), second.Select(p => p.Metadata.Name));
    }

    private static void AssertLabels(V1ObjectMeta metadata)
    {
        Assert.Equal(SessionId, metadata.Labels["agenthub.dev/session"]);
        Assert.Equal("true", metadata.Labels[NetworkPolicyFactory.ResourceLabel]);
    }

    private static void AssertSelector(V1LabelSelector? selector, string component)
    {
        Assert.NotNull(selector);
        Assert.Equal(SessionId, selector!.MatchLabels["agenthub.dev/session"]);
        Assert.Equal(component, selector.MatchLabels["agenthub.dev/component"]);
    }
}
