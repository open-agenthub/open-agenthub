using AgentHub.Api.Containers;
using AgentHub.Api.Services;
using k8s.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionContainerSpecFactoryTests
{
    private const string SessionId = "abc123def456";
    private const string SessionsNamespace = "agenthub-sessions";
    private static readonly SessionContainerOptions Options = new()
    {
        Enabled = true, HubId = "agenthub", NamespacePrefix = "agenthub-sessions-",
        ClusterRoleName = "agenthub-session-containers", EgressPorts = "443, 22, 80, 3900"
    };

    private static ContainerRunRequest Postgres() => new()
    {
        Name = "db", Image = "postgres:17", Ports = [5432],
        Env = new Dictionary<string, string> { ["POSTGRES_PASSWORD"] = "test" }
    };

    [Fact]
    public void Namespace_EnforcesRestrictedAndCarriesTheHubAndSessionLabels()
    {
        var ns = Build(asRoot: false).Namespace;

        Assert.Equal("agenthub-sessions-abc123def456", ns.Metadata.Name);
        Assert.Equal("restricted", ns.Metadata.Labels["pod-security.kubernetes.io/enforce"]);
        Assert.Equal("latest", ns.Metadata.Labels["pod-security.kubernetes.io/enforce-version"]);
        Assert.Equal("agenthub", ns.Metadata.Labels[SessionContainerSpecFactory.HubLabel]);
        Assert.Equal(SessionId, ns.Metadata.Labels[SessionContainerSpecFactory.SessionLabel]);
        Assert.Equal("u-owner", ns.Metadata.Labels[SessionContainerSpecFactory.OwnerLabel]);
    }

    [Fact]
    public void Namespace_OfARootSessionIsBaselineNeverPrivileged()
    {
        var ns = Build(asRoot: true).Namespace;

        Assert.Equal("baseline", ns.Metadata.Labels["pod-security.kubernetes.io/enforce"]);
        Assert.DoesNotContain(ns.Metadata.Labels.Values, v => v == "privileged");
    }

    [Fact]
    public void RoleBinding_GrantsOnlyThePerNamespaceRoleToTheBackend()
    {
        var binding = Build(asRoot: false).RoleBinding;

        Assert.Equal("agenthub-sessions-abc123def456", binding.Metadata.NamespaceProperty);
        Assert.Equal(("ClusterRole", "agenthub-session-containers"), (binding.RoleRef.Kind, binding.RoleRef.Name));
        var subject = Assert.Single(binding.Subjects);
        Assert.Equal(("ServiceAccount", "agenthub-backend", "agenthub"), (subject.Kind, subject.Name, subject.NamespaceProperty));
    }

    [Fact]
    public void Quota_CapsPodsComputeStorageAndForbidsClaimsAndExposedServices()
    {
        var hard = Build(asRoot: false).Quota.Spec.Hard;

        Assert.Equal("5", hard["pods"].ToString());
        Assert.Equal("2", hard["requests.cpu"].ToString());
        Assert.Equal("2", hard["limits.cpu"].ToString());
        Assert.Equal("4Gi", hard["limits.memory"].ToString());
        Assert.Equal("10Gi", hard["limits.ephemeral-storage"].ToString());
        Assert.Equal("0", hard["persistentvolumeclaims"].ToString());
        Assert.Equal("0", hard["services.nodeports"].ToString());
        Assert.Equal("0", hard["services.loadbalancers"].ToString());
    }

    [Fact]
    public void LimitRange_CapsEveryContainer()
    {
        var item = Assert.Single(Build(asRoot: false).Limits.Spec.Limits);

        Assert.Equal("Container", item.Type);
        Assert.Equal("1", item.Max["cpu"].ToString());
        Assert.Equal("2Gi", item.Max["memory"].ToString());
    }

    [Fact]
    public void Policies_DenyEverythingThenAdmitOnlyTheSessionPodAndOwnHelpers()
    {
        var policies = Build(asRoot: false).Policies.ToDictionary(p => p.Metadata.Name);

        var deny = policies["default-deny"];
        Assert.Empty(deny.Spec.PodSelector.MatchLabels ?? new Dictionary<string, string>());
        Assert.Equal(["Ingress", "Egress"], deny.Spec.PolicyTypes);
        Assert.Null(deny.Spec.Ingress);
        Assert.Null(deny.Spec.Egress);

        var ingress = Assert.Single(policies["allow-from-session"].Spec.Ingress);
        Assert.Null(ingress.Ports);
        Assert.Equal(2, ingress.FromProperty.Count);
        var session = ingress.FromProperty[0];
        Assert.Equal(SessionsNamespace, session.NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Equal(SessionId, session.PodSelector.MatchLabels["agenthub.dev/session"]);
        Assert.Equal("agent", session.PodSelector.MatchLabels["agenthub.dev/component"]);
        var sameNamespace = ingress.FromProperty[1];
        Assert.Null(sameNamespace.NamespaceSelector);
        Assert.NotNull(sameNamespace.PodSelector);
        Assert.All(policies.Values, p => Assert.Equal("agenthub-sessions-abc123def456", p.Metadata.NamespaceProperty));
    }

    [Fact]
    public void Egress_IsTheSessionPodsPortListPlusDnsAndNothingElse()
    {
        var egress = Build(asRoot: false).Policies.Single(p => p.Metadata.Name == "allow-egress").Spec.Egress;

        var dns = egress[0];
        Assert.Null(dns.To);
        Assert.Equal(["UDP/53", "TCP/53"], dns.Ports.Select(p => $"{p.Protocol}/{p.Port.Value}"));
        Assert.NotNull(Assert.Single(egress[1].To).PodSelector);
        Assert.Null(egress[1].Ports);
        var web = egress[2];
        Assert.Null(web.To);
        Assert.Equal(["443", "22", "80", "3900"], web.Ports.Select(p => p.Port.Value));
        // The backend's port is opened for agent pods only (allow-agent-to-backend); a helper
        // must not get it through the shared list.
        Assert.DoesNotContain(egress.SelectMany(r => r.Ports ?? []), p => p.Port.Value == "8080");
    }

    [Fact]
    public void SessionEgress_LetsOnlyThisSessionPodReachItsHelperNamespace()
    {
        var policy = Build(asRoot: false).SessionEgress;

        Assert.Equal(SessionsNamespace, policy.Metadata.NamespaceProperty);
        Assert.Equal("true", policy.Metadata.Labels[SessionContainerSpecFactory.SessionEgressLabel]);
        Assert.Equal(SessionId, policy.Metadata.Labels["agenthub.dev/session"]);
        Assert.Equal(SessionId, policy.Spec.PodSelector.MatchLabels["agenthub.dev/session"]);
        Assert.Equal("agent", policy.Spec.PodSelector.MatchLabels["agenthub.dev/component"]);
        Assert.Equal(["Egress"], policy.Spec.PolicyTypes);
        var peer = Assert.Single(Assert.Single(policy.Spec.Egress).To);
        Assert.Equal("agenthub-sessions-abc123def456", peer.NamespaceSelector.MatchLabels["kubernetes.io/metadata.name"]);
        Assert.Null(peer.PodSelector);
    }

    [Fact]
    public void Pod_CopiesTheSessionSecurityContext()
    {
        var (pod, _) = SessionContainerSpecFactory.BuildPod(Options, new AgentPodRuntimeSettings(), SessionId, false, Postgres());
        var container = Assert.Single(pod.Spec.Containers);

        AssertEquivalent(AgentPodSpecFactory.PodSecurity(false), pod.Spec.SecurityContext);
        AssertEquivalent(AgentPodSpecFactory.ContainerSecurity(false), container.SecurityContext);
        Assert.True(pod.Spec.SecurityContext.RunAsNonRoot);
        Assert.Equal(1000, pod.Spec.SecurityContext.RunAsUser);
        Assert.Equal("RuntimeDefault", pod.Spec.SecurityContext.SeccompProfile.Type);
        Assert.False(container.SecurityContext.AllowPrivilegeEscalation);
        Assert.True(container.SecurityContext.ReadOnlyRootFilesystem);
        Assert.Equal(["ALL"], container.SecurityContext.Capabilities.Drop);
        Assert.Null(container.SecurityContext.Capabilities.Add);
        Assert.Null(container.SecurityContext.Privileged);
    }

    [Fact]
    public void Pod_HasNoTokenNoHostAccessAndNoVolumeButEmptyDir()
    {
        var request = Postgres() with { Volumes = [new ContainerVolumeRequest("/var/lib/postgresql/data", "1Gi")] };
        var (pod, _) = SessionContainerSpecFactory.BuildPod(Options, new AgentPodRuntimeSettings(), SessionId, false, request);

        Assert.False(pod.Spec.AutomountServiceAccountToken);
        Assert.False(pod.Spec.EnableServiceLinks);
        Assert.Null(pod.Spec.ServiceAccountName);
        Assert.Null(pod.Spec.HostNetwork);
        Assert.Null(pod.Spec.HostPID);
        Assert.Null(pod.Spec.HostIPC);
        Assert.Null(pod.Spec.NodeName);
        Assert.Null(pod.Spec.ImagePullSecrets);
        Assert.All(pod.Spec.Volumes, v =>
        {
            Assert.NotNull(v.EmptyDir);
            Assert.NotNull(v.EmptyDir.SizeLimit);
            Assert.Null(v.HostPath);
            Assert.Null(v.PersistentVolumeClaim);
            Assert.Null(v.Secret);
        });
        Assert.Equal(["/tmp", "/var/lib/postgresql/data"], pod.Spec.Containers[0].VolumeMounts.Select(m => m.MountPath));
        Assert.Equal("1Gi", pod.Spec.Volumes[1].EmptyDir.SizeLimit.ToString());
    }

    [Fact]
    public void Pod_OfARootSessionGetsTheRootSessionsContextAndNoMore()
    {
        var (pod, _) = SessionContainerSpecFactory.BuildPod(Options, new AgentPodRuntimeSettings(), SessionId, true, Postgres());

        AssertEquivalent(AgentPodSpecFactory.PodSecurity(true), pod.Spec.SecurityContext);
        AssertEquivalent(AgentPodSpecFactory.ContainerSecurity(true), pod.Spec.Containers[0].SecurityContext);
        Assert.False(pod.Spec.Containers[0].SecurityContext.AllowPrivilegeEscalation);
        Assert.Null(pod.Spec.Containers[0].SecurityContext.Privileged);
    }

    [Fact]
    public void Pod_WritableRootIsTheOnlyRelaxation()
    {
        var (pod, _) = SessionContainerSpecFactory.BuildPod(Options, new AgentPodRuntimeSettings(), SessionId, false,
            Postgres() with { WritableRootFilesystem = true });
        var security = pod.Spec.Containers[0].SecurityContext;

        Assert.False(security.ReadOnlyRootFilesystem);
        Assert.True(security.RunAsNonRoot);
        Assert.Equal(["ALL"], security.Capabilities.Drop);
    }

    [Fact]
    public void Pod_RequestsWhatItLimitsAndDefaultsFromOptions()
    {
        var (pod, _) = SessionContainerSpecFactory.BuildPod(Options, new AgentPodRuntimeSettings(), SessionId, false, Postgres());
        var resources = pod.Spec.Containers[0].Resources;

        Assert.Equal("250m", resources.Limits["cpu"].ToString());
        Assert.Equal("256Mi", resources.Limits["memory"].ToString());
        Assert.Equal("2Gi", resources.Limits["ephemeral-storage"].ToString());
        Assert.Equal(resources.Limits.Select(kv => (kv.Key, kv.Value.ToString())),
            resources.Requests.Select(kv => (kv.Key, kv.Value.ToString())));
    }

    [Fact]
    public void Service_IsClusterIpOnTheRequestedPortsSelectingOnlyThisHelper()
    {
        var (pod, service) = SessionContainerSpecFactory.BuildPod(Options, new AgentPodRuntimeSettings(), SessionId, false, Postgres());

        Assert.NotNull(service);
        Assert.Equal("db", service.Metadata.Name);
        Assert.Equal("ClusterIP", service.Spec.Type);
        Assert.Equal(5432, Assert.Single(service.Spec.Ports).Port);
        Assert.Equal(SessionId, service.Spec.Selector["agenthub.dev/session"]);
        Assert.Equal("db", service.Spec.Selector["agenthub.dev/container"]);
        Assert.All(service.Spec.Selector, kv => Assert.Equal(kv.Value, pod.Metadata.Labels[kv.Key]));
        Assert.Equal("db.agenthub-sessions-abc123def456.svc",
            SessionContainerSpecFactory.HostName("db", "agenthub-sessions-abc123def456"));
    }

    [Fact]
    public void NoPorts_NoService()
    {
        var (_, service) = SessionContainerSpecFactory.BuildPod(Options, new AgentPodRuntimeSettings(), SessionId, false,
            Postgres() with { Ports = [] });
        Assert.Null(service);
    }

    [Theory]
    [InlineData("postgres:17", "docker.io")]
    [InlineData("library/postgres", "docker.io")]
    [InlineData("index.docker.io/library/postgres", "docker.io")]
    [InlineData("registry.example.com/team/app:1", "registry.example.com")]
    [InlineData("localhost:5000/app", "localhost:5000")]
    [InlineData("localhost/app", "localhost")]
    [InlineData("docker.io.example.com/x", "docker.io.example.com")]
    public void RegistryOf_ResolvesLikeTheContainerRuntime(string image, string registry) =>
        Assert.Equal(registry, SessionContainerSpecFactory.RegistryOf(image));

    [Fact]
    public void AllowList_RefusesOtherRegistriesIncludingLookalikes()
    {
        var options = new SessionContainerOptions { AllowedRegistries = "docker.io, registry.example.com" };

        SessionContainerSpecFactory.ValidateImage("postgres:17", options);
        SessionContainerSpecFactory.ValidateImage("registry.example.com/app@sha256:" + new string('a', 64), options);
        Assert.Throws<ContainerRequestException>(() => SessionContainerSpecFactory.ValidateImage("docker.io.example.com/x", options));
        Assert.Throws<ContainerRequestException>(() => SessionContainerSpecFactory.ValidateImage("other.example.com/x", options));
    }

    [Fact]
    public void RequireDigest_RefusesTags()
    {
        var options = new SessionContainerOptions { RequireDigest = true };

        Assert.Throws<ContainerRequestException>(() => SessionContainerSpecFactory.ValidateImage("postgres:17", options));
        SessionContainerSpecFactory.ValidateImage("postgres@sha256:" + new string('0', 64), options);
    }

    public static TheoryData<ContainerRunRequest> Invalid => new()
    {
        Postgres() with { Name = "DB" },
        Postgres() with { Name = "1db" },
        Postgres() with { Name = "db_1" },
        Postgres() with { Name = new string('a', 41) },
        Postgres() with { Image = "postgres;rm -rf /" },
        Postgres() with { Image = "" },
        Postgres() with { Cpu = "2" },
        Postgres() with { Memory = "3Gi" },
        Postgres() with { Cpu = "lots" },
        Postgres() with { Cpu = "0" },
        Postgres() with { Ports = [0] },
        Postgres() with { Ports = [5432, 5432] },
        Postgres() with { Ports = Enumerable.Range(1, 9).ToList() },
        Postgres() with { Env = new Dictionary<string, string> { ["1BAD"] = "x" } },
        Postgres() with { Volumes = [new ContainerVolumeRequest("relative")] },
        Postgres() with { Volumes = [new ContainerVolumeRequest("/data/../etc")] },
        Postgres() with { Volumes = [new ContainerVolumeRequest("/tmp")] },
        Postgres() with { Volumes = [new ContainerVolumeRequest("/proc/x")] },
        Postgres() with { Volumes = [new ContainerVolumeRequest("/dev")] },
        Postgres() with { Volumes = [new ContainerVolumeRequest("/data", "3Gi")] },
        Postgres() with { Volumes = [new ContainerVolumeRequest("/a"), new("/b"), new("/c"), new("/d"), new("/e")] },
        Postgres() with { Volumes = [new ContainerVolumeRequest("/a"), new("/a/")] },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Validate_RefusesWhatTheInstanceDoesNotAllow(ContainerRunRequest request) =>
        Assert.Throws<ContainerRequestException>(() => SessionContainerSpecFactory.Validate(request, Options));

    [Fact]
    public void Validate_AcceptsAFullRequestAtTheLimits()
    {
        SessionContainerSpecFactory.Validate(Postgres() with
        {
            Name = "a" + new string('b', 39), Cpu = "1", Memory = "2Gi",
            Command = ["docker-entrypoint.sh"], Args = ["postgres", "-c", "fsync=off"],
            Volumes = [new ContainerVolumeRequest("/var/lib/postgresql/data", "2Gi")]
        }, Options);
    }

    [Fact]
    public void ParsePorts_DropsWhatIsNotAPort() =>
        Assert.Equal([443, 22, 3900], SessionContainerSpecFactory.ParsePorts("443, 22, nope, 0, 70000, 3900, 443"));

    private static SessionNamespaceResources Build(bool asRoot) =>
        SessionContainerSpecFactory.BuildNamespace(Options, SessionsNamespace, SessionId, "u-owner", asRoot);

    private static void AssertEquivalent<T>(T expected, T actual) =>
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected), System.Text.Json.JsonSerializer.Serialize(actual));
}
