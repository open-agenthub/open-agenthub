using System.Collections.Concurrent;
using System.Net;
using AgentHub.Api.Browser;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed partial class KubernetesBrowserServiceTests
{
    [PostgreSqlFact]
    public async Task ReadinessWait_DoesNotOccupyLockPool_AcrossReplicasAndSessions()
    {
        await using var db = await BrowserDatabase.CreateAsync();
        var cluster = new DelayedBrowserCluster();
        var services = Enumerable.Range(0, 3).Select(_ => db.Service(cluster)).ToArray();
        var starts = Enumerable.Range(0, 24).Select(i => services[i % 3].EnsureAsync(
            Session($"s-{i}"), IPAddress.Loopback)).ToArray();
        try
        {
            await UntilAsync(() => cluster.Pods.Count == 24);
            // Timeout=2: keeping even one pool connection per readiness wait would strand
            // the other 22 starts. Holding readiness beyond it reproduces the original failure.
            await Task.Delay(2500);
            Assert.All(starts, task => Assert.False(task.IsCompleted));
            cluster.Ready = true;
            Assert.Equal(24, (await Task.WhenAll(starts)).Length);
            Assert.Equal(24, cluster.Creates);
        }
        finally { cluster.Ready = true; await ObserveAsync(starts); }
    }

    [PostgreSqlFact]
    public async Task ConcurrentStarts_AcrossReplicas_AdoptOnePendingPod()
    {
        await using var db = await BrowserDatabase.CreateAsync();
        var cluster = new DelayedBrowserCluster();
        var starts = Enumerable.Range(0, 6).Select(_ => db.Service(cluster).EnsureAsync(
            Session(), IPAddress.Loopback)).ToArray();
        try
        {
            await UntilAsync(() => cluster.Reads >= 6);
            Assert.Equal(1, cluster.Creates);
            cluster.Ready = true;
            await Task.WhenAll(starts);
            Assert.Equal(1, cluster.Creates);
        }
        finally { cluster.Ready = true; await ObserveAsync(starts); }
    }

    [PostgreSqlFact]
    public async Task StopDuringReadiness_RejectsOldWaiter_AndStartDuringStopWaits()
    {
        await using var db = await BrowserDatabase.CreateAsync();
        var cluster = new DelayedBrowserCluster { HoldRead = true, HoldDelete = true };
        var first = db.Service(cluster).EnsureAsync(Session(), IPAddress.Loopback);
        await cluster.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = db.Service(cluster).StopAsync(Session().Id);
        await cluster.DeleteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = db.Service(cluster).EnsureAsync(Session(), IPAddress.Loopback);
        Assert.Equal(1, cluster.Creates);
        cluster.AllowDelete.TrySetResult();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await UntilAsync(() => cluster.Creates == 2);
        cluster.Ready = true;
        cluster.AllowRead.TrySetResult();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.Contains("stopped or superseded", error.Message);
        await replacement;
        Assert.Single(cluster.Pods);
        Assert.Equal(1, cluster.Deletes);
        Assert.Equal(BrowserPhase.Running, (await db.Leases.GetBySessionAsync(Session().Id))!.Phase);
    }

    [PostgreSqlFact]
    public async Task StopDuringReadiness_LeavesNoPod_AndWaiterReportsStopped()
    {
        await using var db = await BrowserDatabase.CreateAsync();
        var cluster = new DelayedBrowserCluster();
        var start = db.Service(cluster).EnsureAsync(Session(), IPAddress.Loopback);
        await cluster.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await db.Service(cluster).StopAsync(Session().Id).WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => start);
        Assert.Contains("stopped or superseded", error.Message);
        Assert.Empty(cluster.Pods);
        Assert.Null(await db.Leases.GetBySessionAsync(Session().Id));
    }

    [PostgreSqlFact]
    public async Task CancelledOldWaiter_DoesNotDeleteReplacement()
    {
        await using var db = await BrowserDatabase.CreateAsync();
        var cluster = new DelayedBrowserCluster { HoldRead = true };
        using var cancel = new CancellationTokenSource();
        var first = db.Service(cluster).EnsureAsync(Session(), IPAddress.Loopback, cancel.Token);
        await cluster.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await db.Service(cluster).StopAsync(Session().Id);
        var replacement = db.Service(cluster).EnsureAsync(Session(), IPAddress.Loopback);
        await UntilAsync(() => cluster.Creates == 2);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Single(cluster.Pods);
        Assert.Equal(1, cluster.Deletes);
        cluster.Ready = true;
        cluster.AllowRead.TrySetResult();
        await replacement;
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static async Task ObserveAsync(Task[] tasks)
    {
        // A failed assertion still releases and observes every start; cleanup failures must
        // not replace the original regression failure.
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (NpgsqlException error) { System.Diagnostics.Trace.WriteLine(error); }
        catch (TimeoutException error) { System.Diagnostics.Trace.WriteLine(error); }
        catch (InvalidOperationException error) { System.Diagnostics.Trace.WriteLine(error); }
        catch (OperationCanceledException error) { System.Diagnostics.Trace.WriteLine(error); }
    }

    private sealed class DelayedBrowserCluster : IBrowserClusterClient
    {
        public ConcurrentDictionary<string, string> Pods { get; } = new();
        public volatile bool Ready;
        public bool HoldRead, HoldDelete;
        public int Creates, Reads, Deletes;
        public readonly TaskCompletionSource ReadEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource DeleteEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowDelete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task CreateAsync(BrowserPodResources resources, CancellationToken ct = default)
        {
            var labels = resources.Pod.Metadata.Labels;
            Assert.True(Pods.TryAdd(labels["agenthub.dev/session"], labels[BrowserPodSpecFactory.LeaseLabel]));
            Interlocked.Increment(ref Creates);
            return Task.CompletedTask;
        }
        public async Task<BrowserPodSnapshot?> GetAsync(string ns, string id, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            var lease = Pods.GetValueOrDefault(id);
            ReadEntered.TrySetResult();
            if (HoldRead) await AllowRead.Task.WaitAsync(ct);
            return lease is null ? null : new("127.0.0.2", Ready, null, lease);
        }
        public async Task DeleteAsync(string ns, string id, CancellationToken ct = default)
        {
            DeleteEntered.TrySetResult();
            if (HoldDelete) await AllowDelete.Task.WaitAsync(ct);
            Pods.TryRemove(id, out _);
            Interlocked.Increment(ref Deletes);
        }
        public Task<IReadOnlyCollection<string>> ListSessionIdsAsync(string ns, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<string>>(Pods.Keys.ToArray());
    }

    private sealed class BrowserDatabase : IAsyncDisposable
    {
        private readonly string admin;
        private readonly string schema;
        private readonly IConfiguration config;
        public PostgresBrowserLeaseStore Leases { get; }
        private BrowserDatabase(string admin, string schema, IConfiguration config)
        { this.admin = admin; this.schema = schema; this.config = config; Leases = new(config); }
        public static async Task<BrowserDatabase> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING")!;
            var schema = "browser_" + Guid.NewGuid().ToString("N");
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}; CREATE TABLE {schema}.sessions (id text PRIMARY KEY); INSERT INTO {schema}.sessions VALUES ('session-1'); INSERT INTO {schema}.sessions SELECT 's-' || i FROM generate_series(0,23) i", connection);
            await command.ExecuteNonQueryAsync();
            var cs = new NpgsqlConnectionStringBuilder(admin) { SearchPath = schema, Timeout = 2 }.ConnectionString;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["ConnectionStrings:Postgres"] = cs,
                ["Postgres:LockPoolSize"] = "2",
                ["Browser:StartupTimeoutSeconds"] = "15"
            }).Build();
            var db = new BrowserDatabase(admin, schema, config);
            await db.Leases.InitializeAsync();
            return db;
        }
        public KubernetesBrowserService Service(IBrowserClusterClient cluster) =>
            KubernetesBrowserServiceTests.Service(Leases, cluster, new RecordingArtifacts(),
                new PostgresBrowserSessionLock(config), configuration: config);
        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(admin);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
