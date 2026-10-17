using System.Net;
using AgentHub.Api.Containers;
using AgentHub.Api.Controllers;
using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using k8s.Autorest;
using k8s.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionContainerServiceTests
{
    private const string Ns = "agenthub-sessions-abc123def456";

    [Fact]
    public async Task Run_CreatesNamespaceBindingQuotaAndPoliciesBeforeThePod()
    {
        var cluster = new FakeCluster();
        var info = await Service(cluster).RunAsync(Session(), Request());

        Assert.Equal(
        [
            "namespace", "rolebinding", "quota", "limitrange",
            "policy default-deny", "policy allow-from-session", "policy allow-egress",
            "policy session-abc123def456-containers-out", "pod db", "service db"
        ], cluster.Calls.Where(c => !c.StartsWith("list", StringComparison.Ordinal)));
        Assert.Equal("db.agenthub-sessions-abc123def456.svc", info.Host);
        Assert.Equal("restricted", cluster.Namespace!.Metadata.Labels["pod-security.kubernetes.io/enforce"]);
    }

    [Fact]
    public async Task Run_RetriesWhileTheFreshRoleBindingIsNotYetVisible()
    {
        var cluster = new FakeCluster { RbacDenialsBeforeQuota = 2 };
        await Service(cluster).RunAsync(Session(), Request());

        Assert.Equal(3, cluster.Calls.Count(c => c == "quota"));
        Assert.Contains("pod db", cluster.Calls);
    }

    [Fact]
    public async Task Run_SurfacesAnAdmissionRefusalWithoutRetrying()
    {
        var cluster = new FakeCluster
        {
            PodRefusal = """{"kind":"Status","message":"pods \"db\" is forbidden: violates PodSecurity \"restricted:latest\": runAsNonRoot != true"}"""
        };

        var error = await Assert.ThrowsAsync<ContainerRequestException>(() => Service(cluster).RunAsync(Session(), Request()));

        Assert.Contains("violates PodSecurity", error.Message);
        Assert.Equal(1, cluster.Calls.Count(c => c == "pod db"));
    }

    [Fact]
    public async Task Run_RefusesASecondContainerOfTheSameNameAndOneOverTheLimit()
    {
        var cluster = new FakeCluster();
        var service = Service(cluster, maxPods: 2);
        await service.RunAsync(Session(), Request("db"));

        await Assert.ThrowsAsync<ContainerRequestException>(() => service.RunAsync(Session(), Request("db")));
        await service.RunAsync(Session(), Request("cache"));
        var overLimit = await Assert.ThrowsAsync<ContainerRequestException>(() => service.RunAsync(Session(), Request("queue")));
        Assert.Contains("limit is 2", overLimit.Message);
    }

    [Fact]
    public async Task Run_KeepsANamespacesPsaLevelEvenIfTheSessionTurnedRoot()
    {
        // The namespace was made while the session was unprivileged; the switch to root applies
        // at the session's next start, by which time a pause has removed this namespace.
        var cluster = new FakeCluster();
        var service = Service(cluster);
        await service.RunAsync(Session(), Request("db"));

        await service.RunAsync(Session(runAsRoot: true), Request("cache"));

        Assert.True(cluster.Pods["cache"].Spec.SecurityContext.RunAsNonRoot);
    }

    [Fact]
    public async Task Run_OfARootSessionBuildsABaselineNamespaceAndRootHelpers()
    {
        var cluster = new FakeCluster();
        await Service(cluster).RunAsync(Session(runAsRoot: true), Request());

        Assert.Equal("baseline", cluster.Namespace!.Metadata.Labels["pod-security.kubernetes.io/enforce"]);
        Assert.False(cluster.Pods["db"].Spec.SecurityContext.RunAsNonRoot);
    }

    [Fact]
    public async Task Run_WaitsOutATerminatingNamespaceAndRefusesAForeignOne()
    {
        var terminating = new FakeCluster { Namespace = NamespaceWith("agenthub", "abc123def456", DateTime.UtcNow) };
        var error = await Assert.ThrowsAsync<ContainerRequestException>(() => Service(terminating).RunAsync(Session(), Request()));
        Assert.Contains("still being removed", error.Message);

        var foreign = new FakeCluster { Namespace = NamespaceWith("other-hub", "abc123def456", null) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(foreign).RunAsync(Session(), Request()));
        Assert.Empty(foreign.Pods);
    }

    [Fact]
    public async Task Disabled_RefusesEverything()
    {
        var service = Service(new FakeCluster(), enabled: false);
        await Assert.ThrowsAsync<SessionContainersDisabledException>(() => service.RunAsync(Session(), Request()));
        await Assert.ThrowsAsync<SessionContainersDisabledException>(() => service.ListAsync(Session()));
    }

    [Fact]
    public async Task Cleanup_DeletesTheNamespaceAndTheSessionEgressPolicy()
    {
        var cluster = new FakeCluster();
        await Service(cluster).CleanupSessionAsync("abc123def456");

        Assert.Contains($"delete namespace {Ns}", cluster.Calls);
        Assert.Contains("delete policies agenthub-sessions agenthub.dev/session-containers-egress=true,agenthub.dev/session=abc123def456",
            cluster.Calls);
    }

    [Fact]
    public async Task Cleanup_NeverFailsTheSessionDelete()
    {
        var cluster = new FakeCluster { FailDeletes = true };
        await Service(cluster).CleanupSessionAsync("abc123def456");
    }

    [Fact]
    public async Task Stop_RemovesPodAndService()
    {
        var cluster = new FakeCluster();
        var service = Service(cluster);
        await service.RunAsync(Session(), Request());

        Assert.True(await service.StopAsync(Session(), "db"));
        Assert.False(await service.StopAsync(Session(), "db"));
        Assert.Contains($"delete service {Ns}/db", cluster.Calls);
    }

    [Fact]
    public async Task Reconcile_RemovesOnlyNamespacesOfSessionsThatDoNotRun()
    {
        var cluster = new FakeCluster();
        foreach (var (id, _) in new[] { ("running", 0), ("paused", 0), ("gone", 0), ("done", 0) })
            cluster.Labelled.Add(NamespaceWith("agenthub", id, null));
        cluster.Labelled.Add(NamespaceWith("agenthub", "deleting", DateTime.UtcNow));
        var store = new Sessions(
            Session("running", SessionStatus.Running), Session("paused", SessionStatus.Paused),
            Session("done", SessionStatus.Succeeded));

        await new SessionContainerReconcileService(Service(cluster), store, NullLogger<SessionContainerReconcileService>.Instance)
            .ReconcileAsync(CancellationToken.None);

        Assert.Equal(
            ["delete namespace agenthub-sessions-paused", "delete namespace agenthub-sessions-gone", "delete namespace agenthub-sessions-done"],
            cluster.Calls.Where(c => c.StartsWith("delete namespace", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Controller_AdmitsOnlyTheSessionsOwnTokenWhileItRuns()
    {
        var cluster = new FakeCluster();
        Assert.IsType<UnauthorizedResult>(await Controller(null, cluster).List("abc123def456", CancellationToken.None));
        Assert.IsType<ConflictObjectResult>(await Controller(Session(status: SessionStatus.Paused), cluster)
            .List("abc123def456", CancellationToken.None));
        var scheduled = Session();
        scheduled.Mode = SessionMode.Scheduled;
        Assert.IsType<BadRequestObjectResult>(await Controller(scheduled, cluster).List("abc123def456", CancellationToken.None));
        Assert.IsType<NotFoundObjectResult>(await Controller(Session(), cluster, enabled: false)
            .List("abc123def456", CancellationToken.None));
        Assert.DoesNotContain(cluster.Calls, c => !c.StartsWith("list", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Controller_ReturnsTheRefusalReasonToTheAgent()
    {
        var result = await Controller(Session(), new FakeCluster()).Run("abc123def456",
            new SessionContainersController.RunBody("db", "postgres:17", null, null, null, null, "8", null, null, null),
            CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("cpu must be", System.Text.Json.JsonSerializer.Serialize(bad.Value));
    }

    private static SessionContainersController Controller(SessionRecord? session, FakeCluster cluster, bool enabled = true) =>
        new(new FixedAuthorizer(session), Service(cluster, enabled: enabled))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static SessionContainerService Service(FakeCluster cluster, bool enabled = true, int maxPods = 5) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentHub:Namespace"] = "agenthub-sessions",
            ["SessionContainers:Enabled"] = enabled.ToString(),
            ["SessionContainers:HubId"] = "agenthub",
            ["SessionContainers:NamespacePrefix"] = "agenthub-sessions-",
            ["SessionContainers:MaxPods"] = maxPods.ToString()
        }).Build(), cluster, NullLogger<SessionContainerService>.Instance, (_, _) => Task.CompletedTask);

    private static SessionRecord Session(string id = "abc123def456", string status = SessionStatus.Running, bool runAsRoot = false) =>
        new() { Id = id, Owner = "alice", CallbackToken = "token", Status = status, RunAsRoot = runAsRoot };

    private static SessionRecord Session(bool runAsRoot) => Session("abc123def456", SessionStatus.Running, runAsRoot);

    private static ContainerRunRequest Request(string name = "db") => new() { Name = name, Image = "postgres:17", Ports = [5432] };

    private static V1Namespace NamespaceWith(string hub, string session, DateTime? deleting) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = $"agenthub-sessions-{session}", DeletionTimestamp = deleting,
            Labels = new Dictionary<string, string>
            {
                [SessionContainerSpecFactory.HubLabel] = hub,
                [SessionContainerSpecFactory.SessionLabel] = session,
                [SessionContainerSpecFactory.PsaEnforce] = "restricted"
            }
        }
    };

    private static HttpOperationException Forbidden(string content)
    {
        // The wrapper copies status and headers, so the message itself is not needed afterwards.
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
        return new HttpOperationException("forbidden") { Response = new HttpResponseMessageWrapper(response, content) };
    }

    private sealed class FakeCluster : ISessionContainerCluster
    {
        public List<string> Calls { get; } = [];
        public V1Namespace? Namespace { get; set; }
        public List<V1Namespace> Labelled { get; } = [];
        public Dictionary<string, V1Pod> Pods { get; } = [];
        public int RbacDenialsBeforeQuota { get; set; }
        public string? PodRefusal { get; set; }
        public bool FailDeletes { get; set; }

        public Task<V1Namespace?> GetNamespaceAsync(string name, CancellationToken ct = default) => Task.FromResult(Namespace);
        public Task<IReadOnlyList<V1Namespace>> ListNamespacesAsync(string labelSelector, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<V1Namespace>>(Labelled);
        public Task CreateNamespaceAsync(V1Namespace value, CancellationToken ct = default) { Calls.Add("namespace"); Namespace = value; return Task.CompletedTask; }
        public Task CreateRoleBindingAsync(V1RoleBinding value, CancellationToken ct = default) { Calls.Add("rolebinding"); return Task.CompletedTask; }
        public Task CreateQuotaAsync(V1ResourceQuota value, CancellationToken ct = default)
        {
            Calls.Add("quota");
            if (RbacDenialsBeforeQuota-- > 0)
                throw Forbidden("""resourcequotas is forbidden: User "system:serviceaccount:agenthub:agenthub-backend" cannot create resource""");
            return Task.CompletedTask;
        }
        public Task CreateLimitRangeAsync(V1LimitRange value, CancellationToken ct = default) { Calls.Add("limitrange"); return Task.CompletedTask; }
        public Task CreateNetworkPolicyAsync(V1NetworkPolicy value, CancellationToken ct = default) { Calls.Add($"policy {value.Metadata.Name}"); return Task.CompletedTask; }
        public Task<bool> CreatePodAsync(V1Pod value, CancellationToken ct = default)
        {
            Calls.Add($"pod {value.Metadata.Name}");
            if (PodRefusal is not null) throw Forbidden(PodRefusal);
            return Task.FromResult(Pods.TryAdd(value.Metadata.Name, value));
        }
        public Task CreateServiceAsync(V1Service value, CancellationToken ct = default) { Calls.Add($"service {value.Metadata.Name}"); return Task.CompletedTask; }
        public Task<IReadOnlyList<V1Pod>> ListPodsAsync(string namespaceName, string labelSelector, CancellationToken ct = default)
        {
            Calls.Add($"list {labelSelector}");
            var name = labelSelector.Split(',').Select(s => s.Split('=')).FirstOrDefault(s => s[0] == SessionContainerSpecFactory.ContainerLabel)?[1];
            return Task.FromResult<IReadOnlyList<V1Pod>>(Pods.Values.Where(p => name is null || p.Metadata.Name == name).ToList());
        }
        public Task<string?> ReadLogAsync(string namespaceName, string pod, int tailLines, int limitBytes, bool previous, CancellationToken ct = default) =>
            Task.FromResult<string?>("log");
        public Task DeletePodAsync(string namespaceName, string name, CancellationToken ct = default) { Pods.Remove(name); Calls.Add($"delete pod {namespaceName}/{name}"); return Task.CompletedTask; }
        public Task DeleteServiceAsync(string namespaceName, string name, CancellationToken ct = default) { Calls.Add($"delete service {namespaceName}/{name}"); return Task.CompletedTask; }
        public Task DeleteNamespaceAsync(string name, CancellationToken ct = default)
        {
            if (FailDeletes) throw new HttpRequestException("api server unreachable");
            Calls.Add($"delete namespace {name}");
            return Task.CompletedTask;
        }
        public Task DeleteNetworkPoliciesAsync(string namespaceName, string labelSelector, CancellationToken ct = default)
        {
            Calls.Add($"delete policies {namespaceName} {labelSelector}");
            return Task.CompletedTask;
        }
    }

    private sealed class FixedAuthorizer(SessionRecord? record) : IAgentCallbackAuthorizer
    {
        public Task<SessionRecord?> AuthorizeAsync(HttpRequest request, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(record is not null && record.Id == sessionId ? record : null);
    }

    private sealed class Sessions(params SessionRecord[] records) : ISessionStore
    {
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(records.FirstOrDefault(r => r.Id == id));
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
