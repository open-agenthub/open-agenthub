using k8s;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentHub.Api.Admin;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// Require explicitly supplied, disposable storage: this executable creates rows and objects.
var cfg = new ConfigurationBuilder().AddEnvironmentVariables().AddInMemoryCollection(new Dictionary<string, string?>
{ ["AgentHub:Namespace"] = "storage-proof-" + Guid.NewGuid().ToString("N")[..8] }).Build();
foreach (var key in new[] { "S3:ServiceUrl", "S3:Bucket", "S3:AccessKey", "S3:SecretKey", "ConnectionStrings:Postgres" })
    if (string.IsNullOrWhiteSpace(cfg[key])) throw new Exception($"Set {key} for an isolated proof instance.");
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
var store = new PostgresSessionStore(cfg);
await store.InitializeAsync();
var storage = new S3ArtifactStore(cfg);
var locks = new PostgresSessionExpiryLock(cfg);
var cleanup = new SessionStorageCleanup(storage, store, locks, new EphemeralDataProtectionProvider(), NullLogger<SessionStorageCleanup>.Instance);
builder.Services.AddSingleton(cleanup);
builder.Services.AddSingleton(new AdminAccess(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ee:Admins"] = "admin" }).Build(), NullLogger<AdminAccess>.Instance, []));
builder.Services.AddControllers().AddApplicationPart(typeof(AdminSessionStorageController).Assembly);
builder.Services.AddAuthentication("proof").AddScheme<AuthenticationSchemeOptions, ProofAuthentication>("proof", _ => { });
builder.Services.AddAuthorization();
await using var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
// Only the unrelated cluster transport is a fixture; storage, registry, locks, deletion and
// admin controllers below are the backend's real implementations.
app.MapDelete("/api/v1/namespaces/{ns}/{kind}/{name}", () => Results.Json(new { kind = "Status", apiVersion = "v1", status = "Success" }));
app.MapDelete("/apis/batch/v1/namespaces/{ns}/cronjobs/{name}", () => Results.Json(new { kind = "Status", apiVersion = "v1", status = "Success" }));
app.MapGet("/api/v1/namespaces/{ns}/pods", () => Results.Json(new { apiVersion = "v1", kind = "PodList", items = Array.Empty<object>() }));
app.MapGet("/apis/batch/v1/namespaces/{ns}/jobs", () => Results.Json(new { apiVersion = "batch/v1", kind = "JobList", items = Array.Empty<object>() }));
app.MapGet("/api/v1/namespaces/{ns}/pods/{name}", () => Results.NotFound());
app.MapGet("/apis/batch/v1/namespaces/{ns}/cronjobs/{name}", () => Results.NotFound());
await app.StartAsync();
var address = app.Urls.Single();
var realCluster = cfg["SessionStorageProof:KubeConfig"] is { } kubeConfig;
using var cluster = new k8s.Kubernetes(realCluster
    ? k8s.KubernetesClientConfiguration.BuildConfigFromConfigFile(cfg["SessionStorageProof:KubeConfig"])
    : new k8s.KubernetesClientConfiguration { Host = address });
if (realCluster)
{
    await cluster.CoreV1.CreateNamespaceAsync(new k8s.Models.V1Namespace { Metadata = new() { Name = cfg["AgentHub:Namespace"] } });
    try { await cluster.CoreV1.CreateNamespacedServiceAccountAsync(new k8s.Models.V1ServiceAccount { Metadata = new() { Name = "default" } }, cfg["AgentHub:Namespace"]); }
    catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict) { }
}
KubernetesSessionService Service(IArtifactStore artifacts, Kubernetes? clusterClient = null) => new(cfg, store, null!, artifacts,
    DispatchProxy.Create<IBrowserService, NoOp>(), null!, null!, null!, null!, null!,
    new InMemoryEphemeralApiMcpStore(), null!, NullLogger<KubernetesSessionService>.Instance, runtimeUpdateLock: locks,
    cluster: clusterClient ?? cluster);
var service = Service(storage);
var owner = "proof/" + Guid.NewGuid().ToString("N")[..8];
SessionRecord Record(bool expired = false) => new()
{
    Id = Guid.NewGuid().ToString("N")[..12], Owner = owner, CallbackToken = Guid.NewGuid().ToString("N"),
    AutoDeleteAfterSeconds = expired ? 60 : null, AutoDeleteFrom = expired ? SessionExpiry.FromStart : null,
    CreatedAt = DateTime.UtcNow.AddMinutes(-10)
};
string[] Prefixes(SessionRecord r) => [$"sessions/{KubernetesSessionService.OwnerKey(r.Owner)}/{r.Id}/", $"sessions/{Uri.EscapeDataString(r.Owner)}/{r.Id}/"];
async Task Seed(SessionRecord r)
{
    await store.UpsertAsync(r);
    foreach (var prefix in Prefixes(r))
        foreach (var suffix in new[] { "claude-state.tgz", "scrollback.log", "transcript.jsonl", "artifacts/report.txt" })
            await storage.TryPutTextAsync(prefix + suffix, "proof content");
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Gone(SessionRecord r)
{
    Check(await store.GetByIdAsync(r.Id) is null, "Registry row retained after success");
    foreach (var prefix in Prefixes(r)) Check(await storage.HeadAsync(prefix + "scrollback.log") is null, "Object retained after success");
}
var manual = Record();
await Seed(manual);
if (realCluster)
{
    var deniedClusterConfig = KubernetesClientConfiguration.BuildConfigFromConfigFile(cfg["SessionStorageProof:KubeConfig"]);
    deniedClusterConfig.AccessToken = "invalid-proof-token";
    using var deniedCluster = new Kubernetes(deniedClusterConfig);
    try { await Service(storage, deniedCluster).DeleteSessionAsync(owner, manual.Id); throw new Exception("Expected cluster denial"); }
    catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { }
    Check(await store.GetByIdAsync(manual.Id) is not null && await storage.HeadAsync(Prefixes(manual)[0] + "scrollback.log") is not null,
        "Cluster failure discarded session data");
    Console.WriteLine("PASS real cluster denial retains registry and storage");
}
var manualUpload = realCluster ? await FinalUploadAsync(manual, scheduled: false) : Task.CompletedTask;
await service.DeleteSessionAsync(owner, manual.Id);
await manualUpload;
await Gone(manual);
Console.WriteLine("PASS manual deletion through real service: both prefix encodings and registry removed");
var expired = Record(true);
if (realCluster) expired.Mode = AgentHub.Api.Models.SessionMode.Scheduled;
await Seed(expired);
var scheduledUpload = realCluster ? FinalUploadAsync(expired, scheduled: true) : Task.CompletedTask;
var sweep = new SessionExpirySweepService(store, service, locks, [], NullLogger<SessionExpirySweepService>.Instance);
Check(await sweep.SweepOnceAsync(DateTime.UtcNow, default) == 1, "Expiry did not delete");
await scheduledUpload;
await Gone(expired);
Console.WriteLine("PASS expiry through real service and advisory lock: both prefixes removed");
var retry = Record(true);
retry.Mode = AgentHub.Api.Models.SessionMode.Scheduled;
await Seed(retry);
var deniedCfg = new ConfigurationBuilder().AddConfiguration(cfg).AddInMemoryCollection(new Dictionary<string, string?> { ["S3:AccessKey"] = "invalid-proof-key" }).Build();
var deniedService = Service(new S3ArtifactStore(deniedCfg));
try { await deniedService.DeleteSessionAsync(owner, retry.Id); throw new Exception("Expected storage failure"); }
catch (Amazon.S3.AmazonS3Exception) { }
Check(await store.GetByIdAsync(retry.Id) is { Status: SessionStatus.Failed, FailureReason: not null }, "Storage failure lost retry-visible status");
Check(await service.GetSessionAsync(owner, retry.Id) is { Phase: SessionStatus.Failed, FailureReason: not null }, "Stopped scheduled session cannot be fetched for retry");
var deniedSweep = new SessionExpirySweepService(store, deniedService, locks, [], NullLogger<SessionExpirySweepService>.Instance);
Check(await deniedSweep.SweepOnceAsync(DateTime.UtcNow, default) == 0, "Denied expiry reported success");
Check(await store.GetByIdAsync(retry.Id) is not null, "Denied expiry lost registry row");
Check(await sweep.SweepOnceAsync(DateTime.UtcNow, default) == 1, "Restored storage did not retry expiry");
await Gone(retry);
Console.WriteLine("PASS real storage rejection: manual/expiry retain row; restored storage retries successfully");

var live = Record();
var stale = Record();
var orphan = Record();
await Seed(live);
await Seed(stale);
await Seed(orphan);
await store.DeleteAsync(stale.Id);
await store.DeleteAsync(orphan.Id);
// More than one storage page catches pagination errors in prefix deletion.
await Parallel.ForEachAsync(Enumerable.Range(0, 1005), new ParallelOptions { MaxDegreeOfParallelism = 16 },
    async (i, ct) => { await storage.TryPutTextAsync(Prefixes(orphan)[0] + $"artifacts/{i:D4}.txt", "proof", ct); });
var skill = $"skills/{Guid.NewGuid():N}/SKILL.md";
var other = $"proof/{Guid.NewGuid():N}/keep.txt";
await storage.TryPutTextAsync(skill, "keep");
await storage.TryPutTextAsync(other, "keep");
using var http = new HttpClient { BaseAddress = new Uri(address) };
Check((await http.PostAsync("/api/admin/session-storage-cleanup/dry-run", null)).StatusCode == HttpStatusCode.Unauthorized, "Anonymous cleanup permitted");
http.DefaultRequestHeaders.Add("X-Proof-Owner", "user");
Check((await http.PostAsync("/api/admin/session-storage-cleanup/dry-run", null)).StatusCode == HttpStatusCode.Forbidden, "Non-admin cleanup permitted");
http.DefaultRequestHeaders.Remove("X-Proof-Owner");
http.DefaultRequestHeaders.Add("X-Proof-Owner", "admin");
var previewResponse = await http.PostAsync("/api/admin/session-storage-cleanup/dry-run", null);
previewResponse.EnsureSuccessStatusCode();
var preview = (await previewResponse.Content.ReadFromJsonAsync<SessionStorageCleanup.Preview>())!;
Check(preview.Prefixes.Length == 4 && preview.Existing == 2, "Incorrect orphan preview");
Check(await storage.HeadAsync(Prefixes(orphan)[0] + "artifacts/1004.txt") is not null, "Dry run deleted data");
await store.UpsertAsync(stale);
var applyResponse = await http.PostAsJsonAsync("/api/admin/session-storage-cleanup/apply", new { preview.Token });
applyResponse.EnsureSuccessStatusCode();
var result = (await applyResponse.Content.ReadFromJsonAsync<SessionStorageCleanup.Result>())!;
Check(result.Deleted == 2 && result.Skipped == 2 && result.Failed == 0, "Incorrect apply result");
Check(await storage.HeadAsync(Prefixes(orphan)[0] + "artifacts/1004.txt") is null, "Paginated deletion left data");
Check(await storage.HeadAsync(Prefixes(live)[0] + "scrollback.log") is not null, "Existing session deleted");
Check(await storage.HeadAsync(Prefixes(stale)[1] + "scrollback.log") is not null, "Stale preview deleted existing session");
Check(await storage.HeadAsync(skill) is not null && await storage.HeadAsync(other) is not null, "Unrelated prefix deleted");
var remainingPrefixes = new List<string>();
await foreach (var prefix in storage.ListSessionPrefixesAsync()) remainingPrefixes.Add(prefix);
Check(remainingPrefixes.Order().SequenceEqual(Prefixes(live).Concat(Prefixes(stale)).Order()), "Real bucket listing contains unexpected session prefixes");
(await http.PostAsJsonAsync("/api/admin/session-storage-cleanup/apply", new { preview.Token })).EnsureSuccessStatusCode();
Console.WriteLine("PASS real admin HTTP authorization, read-only dry run, stale-plan protection, paginated deletion, idempotence, skills/other prefix isolation");
await service.DeleteSessionAsync(owner, live.Id);
await service.DeleteSessionAsync(owner, stale.Id);
await storage.DeleteAsync(skill);
await storage.DeleteAsync(other);
var withoutStorage = Record();
await store.UpsertAsync(withoutStorage);
await Service(new NullArtifactStore()).DeleteSessionAsync(owner, withoutStorage.Id);
Check(await store.GetByIdAsync(withoutStorage.Id) is null, "No-storage installation cannot delete sessions");
if (realCluster) await cluster.CoreV1.DeleteNamespaceAsync(cfg["AgentHub:Namespace"]);
Console.WriteLine("PASS no-storage session deletion; all session-storage proof assertions; proof data removed");

async Task<Task> FinalUploadAsync(SessionRecord record, bool scheduled)
{
    var ns = cfg["AgentHub:Namespace"]!;
    var podName = $"session-{record.Id}";
    var references = new List<k8s.Models.V1OwnerReference>();
    if (scheduled)
    {
        var spec = new k8s.Models.V1PodSpec { RestartPolicy = "Never", Containers = [new() { Name = "agent", Image = "example.com/agent:proof" }] };
        var cron = await cluster.BatchV1.CreateNamespacedCronJobAsync(new k8s.Models.V1CronJob
        {
            Metadata = new() { Name = podName },
            Spec = new() { Schedule = "0 0 * * *", JobTemplate = new() { Spec = new() { Template = new() { Spec = spec } } } }
        }, ns);
        var job = await cluster.BatchV1.CreateNamespacedJobAsync(new k8s.Models.V1Job
        {
            Metadata = new() { Name = podName + "-run", OwnerReferences = [new() { ApiVersion = "batch/v1", Kind = "CronJob", Name = podName, Uid = cron.Metadata.Uid, BlockOwnerDeletion = true, Controller = true }] },
            Spec = new() { Template = new() { Spec = new() { RestartPolicy = "Never", Containers = [new() { Name = "agent", Image = "example.com/agent:proof" }] } } }
        }, ns);
        references.Add(new() { ApiVersion = "batch/v1", Kind = "Job", Name = job.Metadata.Name, Uid = job.Metadata.Uid, BlockOwnerDeletion = true, Controller = true });
        podName += "-run-pod";
    }
    await cluster.CoreV1.CreateNamespacedPodAsync(new k8s.Models.V1Pod
    {
        Metadata = new() { Name = podName, Labels = new Dictionary<string, string> { ["agenthub.dev/session"] = record.Id },
            Finalizers = ["proof.example.com/final-upload"], OwnerReferences = references },
        Spec = new() { RestartPolicy = "Never", Containers = [new() { Name = "agent", Image = "example.com/agent:proof" }] }
    }, ns);
    try
    {
        await new SessionProducerCleanup(cluster, ns, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(50)).DeleteAsync(record.Id, default);
        throw new Exception("Expected producer termination timeout");
    }
    catch (TimeoutException) { }
    Check(await store.GetByIdAsync(record.Id) is not null && await storage.HeadAsync(Prefixes(record)[0] + "scrollback.log") is not null,
        "Producer timeout discarded session data");
    return UploadAfterTerminationAsync();
    async Task UploadAfterTerminationAsync()
    {
        while (true)
        {
            var pod = await cluster.CoreV1.ReadNamespacedPodAsync(podName, ns);
            if (pod.Metadata.DeletionTimestamp is not null)
            {
                // A finalizer provides deterministic termination latency without a kubelet. The
                // final upload is a real storage request made after the real API accepts DELETE.
                await Task.Delay(600);
                var registryRetained = await store.GetByIdAsync(record.Id) is not null;
                foreach (var prefix in Prefixes(record)) await storage.TryPutTextAsync(prefix + "scrollback.log", "final upload after DELETE");
                Check(registryRetained, "Registry deleted before producer exit");
                pod.Metadata.Finalizers = [];
                await cluster.CoreV1.ReplaceNamespacedPodAsync(pod, podName, ns);
                try { await cluster.CoreV1.DeleteNamespacedPodAsync(podName, ns, gracePeriodSeconds: 0); }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
                Console.WriteLine($"PASS real API producer termination and final storage upload before prefix deletion (scheduled={scheduled})");
                return;
            }
            await Task.Delay(100);
        }
    }
}

public class NoOp : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method!.ReturnType == typeof(Task) ? Task.CompletedTask
            : method.ReturnType == typeof(Task<BrowserSummary>) ? Task.FromResult(BrowserSummary.Stopped)
            : throw new NotSupportedException(method.Name);
}
public class ProofAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var owner = Request.Headers["X-Proof-Owner"].ToString();
        return Task.FromResult(owner.Length == 0 ? AuthenticateResult.NoResult() : AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", owner)], Scheme.Name)), Scheme.Name)));
    }
}
