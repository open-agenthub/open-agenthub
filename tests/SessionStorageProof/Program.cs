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
var cfg = new ConfigurationBuilder().AddEnvironmentVariables().Build();
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
await app.StartAsync();
var address = app.Urls.Single();
KubernetesSessionService Service(IArtifactStore artifacts) => new(cfg, store, null!, artifacts,
    DispatchProxy.Create<IBrowserService, NoOp>(), null!, null!, null!, null!, null!,
    new InMemoryEphemeralApiMcpStore(), null!, NullLogger<KubernetesSessionService>.Instance, runtimeUpdateLock: locks,
    cluster: new k8s.Kubernetes(new k8s.KubernetesClientConfiguration { Host = address }));
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
await service.DeleteSessionAsync(owner, manual.Id);
await Gone(manual);
Console.WriteLine("PASS manual deletion through real service: both prefix encodings and registry removed");
var expired = Record(true);
await Seed(expired);
var sweep = new SessionExpirySweepService(store, service, locks, [], NullLogger<SessionExpirySweepService>.Instance);
Check(await sweep.SweepOnceAsync(DateTime.UtcNow, default) == 1, "Expiry did not delete");
await Gone(expired);
Console.WriteLine("PASS expiry through real service and advisory lock: both prefixes removed");
var retry = Record(true);
await Seed(retry);
var deniedCfg = new ConfigurationBuilder().AddConfiguration(cfg).AddInMemoryCollection(new Dictionary<string, string?> { ["S3:AccessKey"] = "invalid-proof-key" }).Build();
var deniedService = Service(new S3ArtifactStore(deniedCfg));
try { await deniedService.DeleteSessionAsync(owner, retry.Id); throw new Exception("Expected storage failure"); }
catch (Amazon.S3.AmazonS3Exception) { }
Check(await store.GetByIdAsync(retry.Id) is not null, "Storage failure lost registry row");
var deniedSweep = new SessionExpirySweepService(store, deniedService, locks, [], NullLogger<SessionExpirySweepService>.Instance);
Check(await deniedSweep.SweepOnceAsync(DateTime.UtcNow, default) == 0, "Denied expiry reported success");
Check(await store.GetByIdAsync(retry.Id) is not null, "Denied expiry lost registry row");
try { await Service(new NullArtifactStore()).DeleteSessionAsync(owner, retry.Id); throw new Exception("Expected missing configuration failure"); }
catch (InvalidOperationException) { }
Check(await store.GetByIdAsync(retry.Id) is not null, "Missing config lost row");
Check(await sweep.SweepOnceAsync(DateTime.UtcNow, default) == 1, "Restored storage did not retry expiry");
await Gone(retry);
Console.WriteLine("PASS real storage rejection and missing config: manual/expiry retain row; restored storage retries successfully");

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
(await http.PostAsJsonAsync("/api/admin/session-storage-cleanup/apply", new { preview.Token })).EnsureSuccessStatusCode();
Console.WriteLine("PASS real admin HTTP authorization, read-only dry run, stale-plan protection, paginated deletion, idempotence, skills/other prefix isolation");
await service.DeleteSessionAsync(owner, live.Id);
await service.DeleteSessionAsync(owner, stale.Id);
await storage.DeleteAsync(skill);
await storage.DeleteAsync(other);
Console.WriteLine("PASS all session-storage proof assertions; proof data removed");

public class NoOp : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method!.ReturnType == typeof(Task) ? Task.CompletedTask : throw new NotSupportedException(method.Name);
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
