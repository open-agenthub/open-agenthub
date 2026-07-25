using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentHub.Api.Browser;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class KubernetesBrowserServiceTests
{
    [Fact]
    public async Task ConcurrentEnsure_CreatesOnePodAndReturnsOneLease()
    {
        var leases = new MemoryLeaseStore();
        var cluster = new RecordingBrowserCluster();
        var distributedLock = new RecordingBrowserSessionLock();
        var service = Service(leases, cluster, new RecordingArtifacts(), distributedLock);

        var connections = await Task.WhenAll(
            service.EnsureAsync(Session(), IPAddress.Parse("10.0.0.8")), service.EnsureAsync(Session(), IPAddress.Parse("10.0.0.8")));

        Assert.Equal(1, cluster.CreateCalls);
        Assert.Single(leases.Items);
        Assert.Equal(2, distributedLock.AcquireCalls);
        Assert.Equal(1, distributedLock.MaxConcurrent);
        Assert.All(connections, c => Assert.Equal("10.0.0.9", c.PodIp));
    }

    [Fact]
    public async Task CancelledStartup_CleansPodPoliciesAndLease()
    {
        var leases = new MemoryLeaseStore();
        var cluster = new RecordingBrowserCluster
        {
            Get = _ => throw new OperationCanceledException()
        };
        var service = Service(leases, cluster, new RecordingArtifacts());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.EnsureAsync(Session(), IPAddress.Parse("10.0.0.8")));

        Assert.Equal(1, cluster.DeleteCalls);
        Assert.Empty(leases.Items);
    }
    [Fact]
    public async Task MintStateUrls_RequiresExactBrowserToken()
    {
        var leases = new MemoryLeaseStore();
        var rawToken = "browser-secret";
        await leases.TryCreateAsync(BrowserLease.Pending(
            "session-1", "lease-1", SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))));
        var artifacts = new RecordingArtifacts();
        var service = Service(leases, new RecordingBrowserCluster(), artifacts);

        Assert.Null(await service.MintStateUrlsAsync("lease-1", "wrong"));
        var urls = await service.MintStateUrlsAsync("lease-1", rawToken);

        Assert.NotNull(urls);
        Assert.EndsWith("browser-cookies.json", artifacts.LastGetKey);
        Assert.Equal(artifacts.LastGetKey, artifacts.LastPutKey);
    }

    [Fact]
    public async Task Stop_RemovesClusterResourcesAndLease()
    {
        var leases = new MemoryLeaseStore();
        await leases.TryCreateAsync(BrowserLease.Pending("session-1", "lease-1", [1]));
        var cluster = new RecordingBrowserCluster();
        var service = Service(leases, cluster, new RecordingArtifacts());

        await service.StopAsync("session-1");

        Assert.Equal(1, cluster.DeleteCalls);
        Assert.Empty(leases.Items);
    }

    private static KubernetesBrowserService Service(
        IBrowserLeaseStore leases, IBrowserClusterClient cluster, IArtifactStore artifacts,
        IBrowserSessionLock? sessionLock = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentHub:Namespace"] = "sessions-ns",
            ["AgentHub:CallbackBaseUrl"] = "http://backend.control-ns.svc.cluster.local",
            ["Browser:StartupTimeoutSeconds"] = "2"
        }).Build();
        return new KubernetesBrowserService(config, leases, new FixedSessionStore(Session()), artifacts, cluster,
            sessionLock ?? new RecordingBrowserSessionLock(), NullLogger<KubernetesBrowserService>.Instance);
    }

    private static SessionRecord Session() => new()
    {
        Id = "session-1", Owner = "owner", CallbackToken = "callback",
        AgentSessionId = "thread", Mode = SessionMode.Interactive
    };

    private sealed class FixedSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(session.Owner == owner && session.Id == id ? session : null);
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(session.Id == id ? session : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(session.CallbackToken == token ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([session]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingBrowserSessionLock : IBrowserSessionLock
    {
        private readonly SemaphoreSlim gate = new(1, 1);
        private int concurrent;
        public int AcquireCalls;
        public int MaxConcurrent;
        public async Task<IAsyncDisposable> AcquireAsync(string sessionId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref AcquireCalls);
            await gate.WaitAsync(ct);
            var current = Interlocked.Increment(ref concurrent);
            MaxConcurrent = Math.Max(MaxConcurrent, current);
            return new Release(() => { Interlocked.Decrement(ref concurrent); gate.Release(); });
        }
        private sealed class Release(Action release) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class RecordingBrowserCluster : IBrowserClusterClient
    {
        public int CreateCalls;
        public int DeleteCalls;
        public Func<CancellationToken, Task<BrowserPodSnapshot?>> Get { get; init; } = _ =>
            Task.FromResult<BrowserPodSnapshot?>(new("10.0.0.9", true, null));
        public Task CreateAsync(BrowserPodResources resources, CancellationToken ct = default)
        { Interlocked.Increment(ref CreateCalls); return Task.CompletedTask; }
        public Task<BrowserPodSnapshot?> GetAsync(string namespaceName, string sessionId,
            CancellationToken ct = default) => Get(ct);
        public Task DeleteAsync(string namespaceName, string sessionId, CancellationToken ct = default)
        { Interlocked.Increment(ref DeleteCalls); return Task.CompletedTask; }
        public Task<IReadOnlyCollection<string>> ListSessionIdsAsync(string namespaceName,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<string>>([]);
    }

    private sealed class MemoryLeaseStore : IBrowserLeaseStore
    {
        public Dictionary<string, BrowserLease> Items { get; } = new();
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryCreateAsync(BrowserLease lease, CancellationToken ct = default)
        { lock (Items) { if (Items.ContainsKey(lease.SessionId)) return Task.FromResult(false); Items[lease.SessionId] = lease; return Task.FromResult(true); } }
        public Task<BrowserLease?> GetBySessionAsync(string sessionId, CancellationToken ct = default)
        { lock (Items) return Task.FromResult(Items.GetValueOrDefault(sessionId)); }
        public Task<BrowserLease?> GetByLeaseAsync(string leaseId, CancellationToken ct = default)
        { lock (Items) return Task.FromResult(Items.Values.SingleOrDefault(x => x.LeaseId == leaseId)); }
        public Task<IReadOnlyDictionary<string, BrowserLease>> ListBySessionsAsync(
            IReadOnlyCollection<string> sessionIds, CancellationToken ct = default)
        { lock (Items) return Task.FromResult<IReadOnlyDictionary<string, BrowserLease>>(
            Items.Where(x => sessionIds.Contains(x.Key)).ToDictionary()); }
        public Task<IReadOnlyCollection<BrowserLease>> ListAsync(CancellationToken ct = default)
        { lock (Items) return Task.FromResult<IReadOnlyCollection<BrowserLease>>(Items.Values.ToArray()); }
        public Task SetRunningAsync(string leaseId, string podIp, CancellationToken ct = default) =>
            Update(leaseId, x => x with { Phase = BrowserPhase.Running, PodIp = podIp });
        public Task SetFailedAsync(string leaseId, string failureCode, CancellationToken ct = default) =>
            Update(leaseId, x => x with { Phase = BrowserPhase.Failed, FailureCode = failureCode });
        public Task SetStoppingAsync(string leaseId, CancellationToken ct = default) =>
            Update(leaseId, x => x with { Phase = BrowserPhase.Stopping });
        public Task DeleteAsync(string leaseId, CancellationToken ct = default)
        { lock (Items) { var item = Items.SingleOrDefault(x => x.Value.LeaseId == leaseId); if (item.Key is not null) Items.Remove(item.Key); } return Task.CompletedTask; }
        private Task Update(string leaseId, Func<BrowserLease, BrowserLease> update)
        { lock (Items) { var item = Items.Single(x => x.Value.LeaseId == leaseId); Items[item.Key] = update(item.Value); } return Task.CompletedTask; }
    }

    private sealed class RecordingArtifacts : IArtifactStore
    {
        public string? LastGetKey { get; private set; }
        public string? LastPutKey { get; private set; }
        public string PresignPut(string key, TimeSpan ttl) { LastPutKey = key; return "put"; }
        public string PresignGet(string key, TimeSpan ttl) { LastGetKey = key; return "get"; }
        public Task<string?> GetTextAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }
}