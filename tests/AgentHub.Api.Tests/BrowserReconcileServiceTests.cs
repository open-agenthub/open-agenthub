using System.Net;
using AgentHub.Api.Browser;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class BrowserReconcileServiceTests
{
    [Fact]
    public async Task PendingReadyPod_IsPromotedToRunning()
    {
        var lease = BrowserLease.Pending("s1", "l1", [1]);
        var leases = new ReconcileLeaseStore(lease);
        var cluster = new ReconcileCluster { Snapshot = new("10.0.0.9", true, null) };
        var service = Service(leases, cluster, Session(SessionStatus.Running));

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(BrowserPhase.Running, leases.Item?.Phase);
        Assert.Equal("10.0.0.9", leases.Item?.PodIp);
    }

    [Fact]
    public async Task MissingPendingPod_CleansPoliciesAndMarksLeaseFailed()
    {
        var leases = new ReconcileLeaseStore(BrowserLease.Pending("s1", "l1", [1]));
        var cluster = new ReconcileCluster { Snapshot = null };
        var service = Service(leases, cluster, Session(SessionStatus.Running));

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, cluster.DeleteCalls);
        Assert.Equal(BrowserPhase.Failed, leases.Item?.Phase);
    }

    [Theory]
    [InlineData(BrowserPhase.Stopping, SessionStatus.Running)]
    [InlineData(BrowserPhase.Running, SessionStatus.Paused)]
    public async Task NonLiveLease_RetriesFullStop(BrowserPhase phase, string sessionStatus)
    {
        var pending = BrowserLease.Pending("s1", "l1", [1]);
        var leases = new ReconcileLeaseStore(pending with { Phase = phase });
        var cluster = new ReconcileCluster { Snapshot = new("10.0.0.9", true, null) };
        var browser = new ReconcileBrowser(leases, cluster);
        var service = Service(leases, cluster, Session(sessionStatus), browser);

        await service.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, browser.StopCalls);
        Assert.Null(leases.Item);
    }

    private static BrowserReconcileService Service(ReconcileLeaseStore leases, ReconcileCluster cluster,
        SessionRecord session, ReconcileBrowser? browser = null) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AgentHub:Namespace"] = "sessions" }).Build(),
        leases, new ReconcileSessionStore(session), cluster, browser ?? new ReconcileBrowser(leases, cluster),
        new ReconcileLock(), NullLogger<BrowserReconcileService>.Instance);

    private static SessionRecord Session(string status) => new()
    {
        Id = "s1", Owner = "owner", CallbackToken = "token", AgentSessionId = "thread",
        Mode = SessionMode.Interactive, Status = status
    };

    private sealed class ReconcileLeaseStore(BrowserLease? item) : IBrowserLeaseStore
    {
        public BrowserLease? Item { get; private set; } = item;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> TryCreateAsync(BrowserLease lease, CancellationToken ct = default) { Item = lease; return Task.FromResult(true); }
        public Task<BrowserLease?> GetBySessionAsync(string sessionId, CancellationToken ct = default) => Task.FromResult(Item?.SessionId == sessionId ? Item : null);
        public Task<BrowserLease?> GetByLeaseAsync(string leaseId, CancellationToken ct = default) => Task.FromResult(Item?.LeaseId == leaseId ? Item : null);
        public Task<IReadOnlyDictionary<string, BrowserLease>> ListBySessionsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, BrowserLease>>(Item is not null && ids.Contains(Item.SessionId) ? new Dictionary<string, BrowserLease> { [Item.SessionId] = Item } : new Dictionary<string, BrowserLease>());
        public Task<IReadOnlyCollection<BrowserLease>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<BrowserLease>>(Item is null ? [] : [Item]);
        public Task SetRunningAsync(string leaseId, string podIp, CancellationToken ct = default) { if (Item?.LeaseId == leaseId) Item = Item with { Phase = BrowserPhase.Running, PodIp = podIp }; return Task.CompletedTask; }
        public Task SetFailedAsync(string leaseId, string code, CancellationToken ct = default) { if (Item?.LeaseId == leaseId) Item = Item with { Phase = BrowserPhase.Failed, FailureCode = code }; return Task.CompletedTask; }
        public Task SetStoppingAsync(string leaseId, CancellationToken ct = default) { if (Item?.LeaseId == leaseId) Item = Item with { Phase = BrowserPhase.Stopping }; return Task.CompletedTask; }
        public Task DeleteAsync(string leaseId, CancellationToken ct = default) { if (Item?.LeaseId == leaseId) Item = null; return Task.CompletedTask; }
    }

    private sealed class ReconcileSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(session.Owner == owner && session.Id == id ? session : null);
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(session.Id == id ? session : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([session]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ReconcileCluster : IBrowserClusterClient
    {
        public BrowserPodSnapshot? Snapshot { get; init; }
        public int DeleteCalls { get; private set; }
        public Task CreateAsync(BrowserPodResources resources, CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrowserPodSnapshot?> GetAsync(string ns, string id, CancellationToken ct = default) => Task.FromResult(Snapshot);
        public Task DeleteAsync(string ns, string id, CancellationToken ct = default) { DeleteCalls++; return Task.CompletedTask; }
        public Task<IReadOnlyCollection<string>> ListSessionIdsAsync(string ns, CancellationToken ct = default) => Task.FromResult<IReadOnlyCollection<string>>([]);
    }

    private sealed class ReconcileLock : IBrowserSessionLock
    {
        public Task<IAsyncDisposable> AcquireAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable>(new Release());
        private sealed class Release : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class ReconcileBrowser(ReconcileLeaseStore leases, ReconcileCluster cluster) : IBrowserService
    {
        public int StopCalls { get; private set; }
        public Task<BrowserConnection> EnsureAsync(SessionRecord session, IPAddress agentPodIp, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BrowserSummary> GetSummaryAsync(string id, CancellationToken ct = default) => Task.FromResult(BrowserSummary.Stopped);
        public Task<IReadOnlyDictionary<string, BrowserSummary>> GetSummariesAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<string, BrowserSummary>>(new Dictionary<string, BrowserSummary>());
        public Task<BrowserConnection?> GetConnectionAsync(string id, CancellationToken ct = default) => Task.FromResult<BrowserConnection?>(null);
        public async Task StopAsync(string id, CancellationToken ct = default) { StopCalls++; await cluster.DeleteAsync("sessions", id, ct); if (leases.Item is { } lease) await leases.DeleteAsync(lease.LeaseId, ct); }
        public Task<BrowserStateUrls?> MintStateUrlsAsync(string id, string token, CancellationToken ct = default) => Task.FromResult<BrowserStateUrls?>(null);
        public Task DeleteStateAsync(SessionRecord session, CancellationToken ct = default) => Task.CompletedTask;
    }
}
