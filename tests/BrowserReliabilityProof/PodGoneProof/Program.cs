// #240: the REAL KubernetesBrowserService and KubernetesBrowserClusterClient against a REAL
// kube-apiserver (skill rootless-kube-apiserver-admission-proof; kubeconfig in ~/.kube/config).
// There is no kubelet, so this program stands in for one: it marks each new browser pod Running
// and Ready on its own address, and later evicts or deletes it under a Running lease.
// Build against main or the branch with -p:BackendDir=<worktree>/backend.
using System.Net;
using System.Reflection;
using AgentHub.Api.Browser;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

const string ns = "browser-proof";
var k8s = new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile());
try { await k8s.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new() { Name = ns } }); } catch { }
// The chart creates this account; the browser pod spec names it.
try { await k8s.CoreV1.CreateNamespacedServiceAccountAsync(new V1ServiceAccount { Metadata = new() { Name = "agenthub-agent" } }, ns); } catch { }

var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["AgentHub:Namespace"] = ns,
    ["AgentHub:CallbackBaseUrl"] = "http://backend.example.com",
    ["Browser:Image"] = "example.com/browser:proof",
    ["Browser:StartupTimeoutSeconds"] = "20"
}).Build();
var session = new SessionRecord
{
    Id = "s1", Owner = "owner", CallbackToken = "callback", AgentSessionId = "thread", Mode = SessionMode.Interactive
};
var leases = new MemoryLeaseStore();
var service = new KubernetesBrowserService(config, leases, Stub<ISessionStore>.With(session),
    Stub<IArtifactStore>.With(session), new KubernetesBrowserClusterClient(), Stub<IBrowserRuntimeClient>.With(session),
    new NoLock(), NullLogger<KubernetesBrowserService>.Instance);

// The kubelet stand-in: every browser pod it has not seen yet becomes Running and Ready.
var nextIp = 5;
var seen = new HashSet<string>();
using var stop = new CancellationTokenSource();
var kubelet = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        foreach (var pod in (await k8s.CoreV1.ListNamespacedPodAsync(ns)).Items)
        {
            if (pod.Metadata.DeletionTimestamp is not null || !seen.Add(pod.Metadata.Uid)) continue;
            await SetStatus(pod.Metadata.Name, "Running", $"10.0.0.{nextIp++}", ready: true);
        }
        await Task.Delay(200);
    }
});

async Task SetStatus(string name, string phase, string? ip, bool ready, string? reason = null)
{
    var pod = await k8s.CoreV1.ReadNamespacedPodAsync(name, ns);
    pod.Status = new V1PodStatus
    {
        Phase = phase, PodIP = ip, Reason = reason,
        Conditions = [new V1PodCondition { Type = "Ready", Status = ready ? "True" : "False" }]
    };
    await k8s.CoreV1.ReplaceNamespacedPodStatusAsync(pod, name, ns);
}

async Task<string> Start()
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return (await service.EnsureAsync(session, IPAddress.Parse("10.0.0.2"), timeout.Token)).CdpEndpoint;
    }
    catch (Exception error) { return $"error: {error.Message}"; }
}
async Task<string> Status()
{
    var summary = await service.GetSummaryAsync(session.Id);
    return $"{summary.Phase}{(summary.FailureCode is null ? "" : $" ({summary.FailureCode})")}";
}
async Task<string> Pod()
{
    try
    {
        var pod = await k8s.CoreV1.ReadNamespacedPodAsync($"browser-{session.Id}", ns);
        return $"{pod.Status?.Phase} {pod.Status?.PodIP} {pod.Status?.Reason}".Trim();
    }
    catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
    { return "none"; }
}
var verdict = new List<string>();
void Log(string step, string value) => Console.WriteLine($"{step,-44} {value}");

var first = await Start();
Log("browser_start", first);
if (!first.StartsWith("http://")) { Console.WriteLine("setup failed"); return 2; }
Log("browser_status", await Status());

foreach (var (label, act) in new (string, Func<Task>)[]
{
    ("evicted (pod stays, phase Failed)", () => SetStatus($"browser-{session.Id}", "Failed", null, false, "Evicted")),
    ("deleted (pod object removed)", () => k8s.CoreV1.DeleteNamespacedPodAsync($"browser-{session.Id}", ns, gracePeriodSeconds: 0))
})
{
    var before = (await service.GetConnectionAsync(session.Id))?.CdpEndpoint;
    await act();
    await Task.Delay(500);
    Console.WriteLine($"--- pod {label}; lease still points at {before}");
    Log("  pod", await Pod());
    var status = await Status();
    Log("  browser_status", status);
    var restarted = await Start();
    Log("  browser_start", restarted);
    Log("  pod", await Pod());
    Log("  browser_status", await Status());
    var recovered = restarted.StartsWith("http://") && restarted != before;
    var honest = !status.StartsWith("Running");
    verdict.Add($"{label}: status {(honest ? "honest" : "STILL Running")}, browser_start {(recovered ? "replaced the pod" : "DID NOT recover")}");
}
stop.Cancel();
await kubelet.ContinueWith(_ => { });
await service.StopAsync(session.Id);
Console.WriteLine();
verdict.ForEach(Console.WriteLine);
var pass = verdict.All(v => !v.Contains("STILL") && !v.Contains("NOT"));
Console.WriteLine(pass ? "VERDICT PASS" : "VERDICT FAIL");
return pass ? 0 : 1;

sealed class NoLock : IBrowserSessionLock
{
    public Task<IAsyncDisposable> AcquireAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult<IAsyncDisposable>(new Done());
    sealed class Done : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
}

// Stores and stand-ins the browser path touches only lightly: a session lookup by id, presigned
// URLs nobody fetches. Anything else answers with a default.
class Stub<T> : DispatchProxy where T : class
{
    SessionRecord? session;
    public static T With(SessionRecord session)
    {
        var proxy = Create<T, Stub<T>>();
        ((Stub<T>)(object)proxy).session = session;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var type = method!.ReturnType;
        if (method.Name == "GetByIdAsync") return Task.FromResult<SessionRecord?>(session);
        if (type == typeof(string)) return "http://storage.example.com/x";
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = type.GetGenericArguments()[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner)
                .Invoke(null, [inner.IsValueType ? Activator.CreateInstance(inner) : null]);
        }
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

sealed class MemoryLeaseStore : IBrowserLeaseStore
{
    readonly Dictionary<string, BrowserLease> items = new();
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> TryCreateAsync(BrowserLease lease, CancellationToken ct = default)
    { lock (items) return Task.FromResult(items.TryAdd(lease.SessionId, lease)); }
    public Task<BrowserLease?> GetBySessionAsync(string sessionId, CancellationToken ct = default)
    { lock (items) return Task.FromResult(items.GetValueOrDefault(sessionId)); }
    public Task<BrowserLease?> GetByLeaseAsync(string leaseId, CancellationToken ct = default)
    { lock (items) return Task.FromResult(items.Values.FirstOrDefault(x => x.LeaseId == leaseId)); }
    public Task<IReadOnlyDictionary<string, BrowserLease>> ListBySessionsAsync(
        IReadOnlyCollection<string> sessionIds, CancellationToken ct = default)
    { lock (items) return Task.FromResult<IReadOnlyDictionary<string, BrowserLease>>(
        items.Where(x => sessionIds.Contains(x.Key)).ToDictionary()); }
    public Task<IReadOnlyCollection<BrowserLease>> ListAsync(CancellationToken ct = default)
    { lock (items) return Task.FromResult<IReadOnlyCollection<BrowserLease>>(items.Values.ToArray()); }
    public Task SetRunningAsync(string leaseId, string podIp, CancellationToken ct = default) =>
        Update(leaseId, x => x with { Phase = BrowserPhase.Running, PodIp = podIp, FailureCode = null });
    public Task SetFailedAsync(string leaseId, string failureCode, CancellationToken ct = default) =>
        Update(leaseId, x => x with { Phase = BrowserPhase.Failed, PodIp = null, FailureCode = failureCode });
    public Task SetStoppingAsync(string leaseId, CancellationToken ct = default) =>
        Update(leaseId, x => x with { Phase = BrowserPhase.Stopping });
    public Task DeleteAsync(string leaseId, CancellationToken ct = default)
    {
        lock (items) foreach (var key in items.Where(x => x.Value.LeaseId == leaseId).Select(x => x.Key).ToList()) items.Remove(key);
        return Task.CompletedTask;
    }
    Task Update(string leaseId, Func<BrowserLease, BrowserLease> update)
    {
        lock (items) foreach (var (key, value) in items.Where(x => x.Value.LeaseId == leaseId).ToList()) items[key] = update(value);
        return Task.CompletedTask;
    }
}
