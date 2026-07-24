using AgentHub.Api.Browser;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class BrowserPodSpecFactoryTests
{
    [Fact]
    public void Build_UsesRestrictedPodSecurityAndBoundedWritableMounts()
    {
        var resources = BrowserPodSpecFactory.Build(Session(), Lease(), Context());
        var pod = resources.Pod.Spec;

        Assert.False(pod.AutomountServiceAccountToken);
        Assert.True(pod.SecurityContext.RunAsNonRoot);
        Assert.Equal(1000, pod.SecurityContext.RunAsUser);
        Assert.Equal("RuntimeDefault", pod.SecurityContext.SeccompProfile.Type);
        Assert.Null(pod.HostNetwork);
        var browser = Assert.Single(pod.Containers);
        Assert.False(browser.SecurityContext.AllowPrivilegeEscalation);
        Assert.True(browser.SecurityContext.ReadOnlyRootFilesystem);
        Assert.Contains("ALL", browser.SecurityContext.Capabilities.Drop);
        Assert.Equal(new[] { "/data", "/dev/shm", "/tmp" },
            browser.VolumeMounts.Select(m => m.MountPath).Order().ToArray());
        Assert.Equal("512Mi", pod.Volumes.Single(v => v.Name == "shm").EmptyDir.SizeLimit.ToString());
    }

    [Fact]
    public void Build_CdpPoliciesRequireExactSessionOnBothDirections()
    {
        var resources = BrowserPodSpecFactory.Build(Session("session-1"), Lease(), Context());

        Assert.Equal("session-1", resources.CdpIngress.Spec.PodSelector.MatchLabels["agenthub.dev/session"]);
        var source = resources.CdpIngress.Spec.Ingress.Single().FromProperty.Single().PodSelector.MatchLabels;
        Assert.Equal("session-1", source["agenthub.dev/session"]);
        Assert.Equal("agent", source["agenthub.dev/component"]);

        Assert.Equal("agent", resources.CdpEgress.Spec.PodSelector.MatchLabels["agenthub.dev/component"]);
        var destination = resources.CdpEgress.Spec.Egress.Single().To.Single().PodSelector.MatchLabels;
        Assert.Equal("session-1", destination["agenthub.dev/session"]);
        Assert.Equal("browser", destination["agenthub.dev/component"]);
    }

    [Fact]
    public void Build_BrowserEgressAllowsDnsWebAndBackendCallbacks()
    {
        var policy = BrowserPodSpecFactory.Build(Session(), Lease(), Context()).BrowserEgress;

        Assert.Equal("browser", policy.Spec.PodSelector.MatchLabels["agenthub.dev/component"]);
        var ports = policy.Spec.Egress
            .SelectMany(rule => rule.Ports ?? [])
            .Select(port => (port.Protocol, port.Port.Value))
            .ToHashSet();
        Assert.Contains(("UDP", "53"), ports);
        Assert.Contains(("TCP", "53"), ports);
        Assert.Contains(("TCP", "80"), ports);
        Assert.Contains(("TCP", "443"), ports);
        Assert.Contains(("TCP", "8443"), ports);
        Assert.Contains(("TCP", "8080"), ports);

        var backend = policy.Spec.Egress.Single(rule =>
            rule.To?.Any(peer => peer.PodSelector?.MatchLabels?.ContainsKey("app") == true) == true);
        var peer = Assert.Single(backend.To);
        Assert.Equal("control-ns", peer.NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal("agenthub-backend", peer.PodSelector.MatchLabels["app"]);
    }

    [Fact]
    public void Build_VncPolicyAcceptsOnlyBackendNamespaceAndPods()
    {
        var policy = BrowserPodSpecFactory.Build(Session(), Lease(), Context()).VncIngress;
        var source = policy.Spec.Ingress.Single().FromProperty.Single();

        Assert.Equal("control-ns", source.NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal("agenthub-backend", source.PodSelector.MatchLabels["app"]);
    }

    [Fact]
    public void Build_DoesNotPutSecretsInMetadata()
    {
        var resources = BrowserPodSpecFactory.Build(Session(), Lease(), Context(leaseToken: "lease-secret"));
        var serialized = System.Text.Json.JsonSerializer.Serialize(resources);

        Assert.DoesNotContain("lease-secret", resources.Pod.Metadata.Labels.Values);
        Assert.DoesNotContain("lease-secret", resources.Pod.Metadata.Annotations?.Values ?? []);
        Assert.Contains("lease-secret", serialized);
    }

    private static SessionRecord Session(string id = "session-1") => new()
    {
        Id = id, Owner = "owner", CallbackToken = "callback", AgentSessionId = "thread",
        Mode = SessionMode.Interactive
    };

    private static BrowserLease Lease() => BrowserLease.Pending(
        "session-1", "lease-1", new byte[] { 1 });

    private static BrowserPodContext Context(string leaseToken = "lease-token") => new()
    {
        Namespace = "sessions-ns",
        ControlNamespace = "control-ns",
        CallbackUrl = "http://backend/internal/browser-leases/lease-1",
        LeaseToken = leaseToken,
        Options = new BrowserOptions { ExtraEgressPorts = [8443] }
    };
}
