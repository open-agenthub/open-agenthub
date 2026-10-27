using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using AgentHub.Api.Admin;
using AgentHub.Api.Controllers;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using AgentHub.Api.Persistence;
using AgentHub.Api.Models;
using AgentHub.Api.Files;
using AgentHub.Api.Library;
using AgentHub.Api.Library.ApiMcpGateway;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var root = args[0];
var builder = WebApplication.CreateBuilder([]);
builder.Configuration.AddEnvironmentVariables();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
var storage = new S3ArtifactStore(builder.Configuration);
var registry = new PostgresSessionStore(builder.Configuration);
await registry.InitializeAsync();
var locks = new PostgresSessionExpiryLock(builder.Configuration);
var clusterConfig = KubernetesClientConfiguration.BuildConfigFromConfigFile(Environment.GetEnvironmentVariable("PROOF_KUBECONFIG"));
using var cluster = new Kubernetes(clusterConfig);
const string ns = "claude-cleanup-proof";
try { await cluster.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = ns } }); }
catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict) { }
builder.Configuration["AgentHub:Namespace"] = ns;
var cleanup = new ClaudeStateCleanup(storage, registry, locks, builder.Configuration, cluster);
builder.Services.AddSingleton(cleanup);
builder.Services.AddSingleton(new SessionStorageCleanup(storage, null!, null!, new EphemeralDataProtectionProvider(), NullLogger<SessionStorageCleanup>.Instance));
builder.Services.AddSingleton(new AdminAccess(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Ee:Admins"] = "admin" }).Build(), NullLogger<AdminAccess>.Instance, []));
builder.Services.AddControllers().AddApplicationPart(typeof(AdminSessionStorageController).Assembly);
builder.Services.AddAuthentication("proof").AddScheme<AuthenticationSchemeOptions, ProofAuthentication>("proof", _ => { });
builder.Services.AddAuthorization();
await using var app = builder.Build();
app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
await app.StartAsync();
using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
const string owner = "archive-owner@example.com";
var prefix = $"sessions/{KubernetesSessionService.OwnerKey(owner)}/proof-316/";
var key = prefix + "claude-state.tgz";
var foreignKey = IArtifactStore.StateKey(KubernetesSessionService.OwnerKey("foreign-owner"), "proof-316");
var rawOwnerKey = IArtifactStore.StateKey(owner, "raw-owner-316");
var original = await File.ReadAllBytesAsync(Path.Combine(root, "old.tgz"));
async Task Put(string k, byte[] bytes) => Check(await storage.TryPutStreamAsync(k, new MemoryStream(bytes), "application/gzip"), "storage configured");
async Task<byte[]> Read(string k) { await using var s = await storage.OpenReadAsync(k) ?? throw new Exception("Missing object"); using var b = new MemoryStream(); await s.CopyToAsync(b); return b.ToArray(); }
try
{
    await registry.UpsertAsync(new SessionRecord { Id = "proof-316", Owner = owner, CallbackToken = "synthetic-callback-only", Status = "Paused" });
    await Put(key, original); await Put(foreignKey, original); await Put(rawOwnerKey, original);
    await storage.TryPutTextAsync(prefix + "codex-state.tgz", "other provider");
    var route = "/api/admin/session-storage-cleanup/claude-backups";
    var body = new AdminClaudeStateCleanupController.CleanupRequest(owner, true);
    Check((await http.PostAsJsonAsync(route, body)).StatusCode == HttpStatusCode.Unauthorized, "anonymous denied");
    http.DefaultRequestHeaders.Add("X-Proof-Owner", "ordinary");
    Check((await http.PostAsJsonAsync(route, body)).StatusCode == HttpStatusCode.Forbidden, "nonadmin denied");
    http.DefaultRequestHeaders.Remove("X-Proof-Owner"); http.DefaultRequestHeaders.Add("X-Proof-Owner", "admin");
    var preview = await http.PostAsJsonAsync(route, body with { Apply = false }); preview.EnsureSuccessStatusCode();
    Check((await preview.Content.ReadFromJsonAsync<ClaudeStateCleanup.Result>())!.Rewritten == 1, "preview candidate");
    Check(Same(original, await Read(key)), "preview is read-only");
    var applied = await http.PostAsJsonAsync(route, body); applied.EnsureSuccessStatusCode();
    var result = (await applied.Content.ReadFromJsonAsync<ClaudeStateCleanup.Result>())!;
    Check(result.Rewritten == 1 && result.Failed == 0, "one rewritten: " + result);
    var cleaned = await Read(key);
    await File.WriteAllBytesAsync(Path.Combine(root, "cleaned.tgz"), cleaned);
    var before = Entries(original); var after = Entries(cleaned);
    Check(before.Keys.Any(Backup), "baseline contains backups");
    Check(!after.Keys.Any(Backup), "backups removed");
    Check(before.Where(e => !Backup(e.Key)).OrderBy(e => e.Key).SequenceEqual(after.OrderBy(e => e.Key)), "retained header, padding and payload hashes identical");
    Check(Same(original, await Read(foreignKey)) && Same(original, await Read(rawOwnerKey)), "hashed owner isolation");
    Check(await storage.GetTextAsync(prefix + "codex-state.tgz") == "other provider", "other provider untouched");
    var second = await cleanup.RunAsync(owner, true, default);
    Check(second.Clean == 1 && second.Rewritten == 0 && Same(cleaned, await Read(key)), "idempotent byte-identical object");
    await Put(key, original);
    await using (var held = await locks.TryAcquireAsync("proof-316") ?? throw new Exception("Lock unavailable"))
        Check((await cleanup.RunAsync(owner, true, default)).Skipped == 1, "held advisory lock skipped");
    var pod = new V1Pod { Metadata = new V1ObjectMeta { Name = "session-proof-316" }, Spec = new V1PodSpec { Containers = [new V1Container { Name = "fixture", Image = "example.com/fixture:local" }] } };
    await cluster.CoreV1.CreateNamespacedPodAsync(pod, ns);
    Check((await cleanup.RunAsync(owner, true, default)).Skipped == 1, "named legacy pod skipped");
    Check(Same(original, await Read(key)), "live producer archive untouched");
    await cluster.CoreV1.DeleteNamespacedPodAsync(pod.Metadata.Name, ns, gracePeriodSeconds: 0);
    var labelled = new V1Pod { Metadata = new V1ObjectMeta { Name = "scheduled-proof-316", Labels = new Dictionary<string,string> { ["agenthub.dev/session"] = "proof-316" }, Finalizers = ["example.com/proof"] }, Spec = pod.Spec };
    await cluster.CoreV1.CreateNamespacedPodAsync(labelled, ns);
    await cluster.CoreV1.DeleteNamespacedPodAsync(labelled.Metadata.Name, ns, gracePeriodSeconds: 0);
    Check((await cleanup.RunAsync(owner, true, default)).Skipped == 1, "terminating labelled producer skipped");
    var terminating = await cluster.CoreV1.ReadNamespacedPodAsync(labelled.Metadata.Name, ns);
    terminating.Metadata.Finalizers = [];
    await cluster.CoreV1.ReplaceNamespacedPodAsync(terminating, terminating.Metadata.Name, ns);
    Check((await cleanup.RunAsync(owner, true, default)).Rewritten == 1, "rewrite after real producer exit");
    await Put(key, original);
    var cron = await cluster.BatchV1.CreateNamespacedCronJobAsync(new V1CronJob {
        Metadata = new V1ObjectMeta { Name = "session-proof-316" },
        Spec = new V1CronJobSpec { Schedule = "0 0 * * *", Suspend = true,
            JobTemplate = new V1JobTemplateSpec { Spec = new V1JobSpec { Template = new V1PodTemplateSpec { Spec = new V1PodSpec { RestartPolicy = "Never", Containers = pod.Spec.Containers } } } } }
    }, ns);
    Check((await cleanup.RunAsync(owner, true, default)).Skipped == 1, "suspended CronJob skipped");
    var job = await cluster.BatchV1.CreateNamespacedJobAsync(new V1Job {
        Metadata = new V1ObjectMeta { Name = "owned-job-316", Finalizers = ["example.com/proof"], OwnerReferences = [new V1OwnerReference { ApiVersion = "batch/v1", Kind = "CronJob", Name = cron.Metadata.Name, Uid = cron.Metadata.Uid }] },
        Spec = cron.Spec.JobTemplate.Spec
    }, ns);
    await cluster.BatchV1.DeleteNamespacedCronJobAsync(cron.Metadata.Name, ns, propagationPolicy: "Background");
    Check((await cleanup.RunAsync(owner, true, default)).Skipped == 1, "owned Job skipped after CronJob deletion");
    job = await cluster.BatchV1.ReadNamespacedJobAsync(job.Metadata.Name, ns);
    job.Metadata.Finalizers = [];
    await cluster.BatchV1.ReplaceNamespacedJobAsync(job, job.Metadata.Name, ns);
    try { await cluster.BatchV1.DeleteNamespacedJobAsync(job.Metadata.Name, ns, propagationPolicy: "Background"); }
    catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { Console.WriteLine("PASS garbage collector already removed completed proof Job"); }
    using (var deniedCluster = new Kubernetes(new KubernetesClientConfiguration { Host = clusterConfig.Host, AccessToken = "synthetic-invalid-token", SkipTlsVerify = true }))
    {
        var denied = new ClaudeStateCleanup(storage, registry, locks, builder.Configuration, deniedCluster);
        Check((await denied.RunAsync(owner, true, default)).Failed == 1, "unknown cluster status fails closed");
        Check(Same(original, await Read(key)), "cluster denial preserves archive");
    }
    var callback = new AgentCallbackAuthorizer(registry);
    var context = new DefaultHttpContext(); context.Request.Headers["X-Agent-Token"] = "synthetic-callback-only";
    Check(await callback.AuthorizeAsync(context.Request, "proof-316") is not null, "live callback token accepted");
    var tokens = new McpGatewayTokenService(new EphemeralDataProtectionProvider());
    var gatewayToken = tokens.Issue("proof-316", "fixture-server", owner);
    Check(tokens.TryValidate(gatewayToken, "fixture-server", out _), "gateway token issued");
    await registry.DeleteAsync("proof-316");
    Check(await callback.AuthorizeAsync(context.Request, "proof-316") is null, "deleted callback token rejected");
    var gateway = new ApiMcpGatewayHandler(null!, null!, tokens, null!, new HttpClient(), registry, null!);
    var gatewayContext = new DefaultHttpContext(); gatewayContext.Request.Method = "POST";
    gatewayContext.Request.Headers[McpGatewayTokenService.HeaderName] = gatewayToken;
    await gateway.HandleCatalogAsync(gatewayContext, "fixture-server");
    Check(gatewayContext.Response.StatusCode == 401, "real gateway rejects deleted session despite valid token");
    await Put(key, [1, 2, 3]);
    Check((await cleanup.RunAsync(owner, true, default)).Failed == 1, "malformed object counted");
    Check((await Read(key)).SequenceEqual(new byte[] { 1, 2, 3 }), "malformed object untouched");
    if (args.Contains("--provider-logins"))
    {
        await HistoricalLogins.RunAsync(root, http, storage, registry, locks, cluster, ns, owner, prefix);
        Check(Same(original, await Read(foreignKey)) && Same(original, await Read(rawOwnerKey)), "provider cleanup owner isolation");
    }
    Console.WriteLine("PASS real S3 + real admin HTTP: authorization, preview, rewrite, owner isolation, idempotence, malformed archive and real producer/lock guards");
    foreach (var item in after) Console.WriteLine($"SHA256 {item.Key} {item.Value}");
}
finally
{
    await registry.DeleteAsync("proof-316");
    await storage.DeleteByPrefixAsync(prefix); await storage.DeleteAsync(foreignKey); await storage.DeleteAsync(rawOwnerKey);
    await app.StopAsync();
}
static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
static bool Backup(string name) => name.TrimEnd('/') == ".claude/backups" || name.StartsWith(".claude/backups/", StringComparison.Ordinal);
static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
static Dictionary<string, string> Entries(byte[] bytes)
{
    using var compressed = new MemoryStream(bytes); using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
    using var raw = new MemoryStream(); gzip.CopyTo(raw); raw.Position = 0;
    using var reader = new TarReader(raw, leaveOpen: true);
    var result = new Dictionary<string, string>(); long start = 0;
    while (reader.GetNextEntry() is { } entry)
    {
        entry.DataStream?.CopyTo(Stream.Null);
        var end = (raw.Position + 511) / 512 * 512;
        result.Add(entry.Name, Convert.ToHexString(SHA256.HashData(raw.GetBuffer().AsSpan((int)start, (int)(end - start)))));
        start = end;
    }
    return result;
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
