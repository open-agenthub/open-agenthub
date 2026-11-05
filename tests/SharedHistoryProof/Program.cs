// Proof host for #427: the production SessionsController and SharedSessionsController, the real
// SessionAccessService, SessionSharingService, SharedSessionHistory, KubernetesSessionService and
// LiveTranscriptReader (over real HTTP through AgentPodRequests), and the real Postgres session,
// user and share stores. The stand-ins: a cookie that says who is signed in, a Kubernetes API
// that reports one running pod at 127.0.0.1, and that "pod": an HTTP server answering
// GET /agenthub/transcript the way common/server.js does. No S3, so the stored transcript is
// only the Postgres tail — the situation of a session whose upload url has expired.
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Library;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using k8s;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;

var connection = Environment.GetEnvironmentVariable("PROOF_POSTGRES")
    ?? "Host=127.0.0.1;Port=54329;Username=postgres;Database=proof427";
const string SessionId = "reviews";
const string CallbackToken = "callback-token-reviews-7f3c9e";
const string McpCredential = "mcp-owner-credential-5b1d2a";

static string Line(string role, string text, DateTime at) => System.Text.Json.JsonSerializer.Serialize(new
{
    type = role, timestamp = at.ToString("O"), message = new { role, content = text }
}) + "\n";
// About 1.6 MB: the first reviews are older than the 1,000,000-character Postgres tail.
var start = DateTime.UtcNow.Date.AddHours(6);
var transcript = new StringBuilder();
transcript.Append(Line("user", "Review 1 of today: approved the parser change", start));
for (var i = 2; i <= 400; i++)
    transcript.Append(Line("assistant", $"Review {i} of today: " + string.Concat(Enumerable.Repeat("checked the diff, ran the suite. ", 120)), start.AddMinutes(i)));
transcript.Append(Line("assistant",
    $"env: AGENTHUB_CALLBACK_TOKEN={CallbackToken}\ncurl -H 'Authorization: Bearer {McpCredential}' https://tracker.example.com",
    start.AddMinutes(401)));
var file = Encoding.UTF8.GetBytes(transcript.ToString());

// The pod: whole lines from a byte offset, the size and the next offset in headers (server.js).
var pod = WebApplication.CreateBuilder();
var podApp = pod.Build();
podApp.Urls.Add("http://127.0.0.1:0");
podApp.MapGet("/agenthub/transcript", (HttpContext http, long from) =>
{
    if (http.Request.Headers["X-Agent-Token"] != CallbackToken) return Results.StatusCode(401);
    var body = from >= file.Length ? [] : file[(int)from..];
    // server.js's MAX_TRANSCRIPT_CHUNK: the reader refuses a larger body and asks again.
    if (body.Length > 8 * 1024 * 1024) body = body[..(8 * 1024 * 1024)];
    var whole = Array.LastIndexOf(body, (byte)'\n') + 1;
    http.Response.Headers["X-Transcript-Size"] = file.Length.ToString();
    http.Response.Headers["X-Transcript-Next"] = (Math.Min(from, file.Length) + whole).ToString();
    return Results.Bytes(body[..whole], "application/x-ndjson");
});
await podApp.StartAsync();
var podPort = new Uri(podApp.Urls.First()).Port;

// An API server that knows one running pod and nothing else.
var kube = WebApplication.CreateBuilder();
var kubeApp = kube.Build();
kubeApp.Urls.Add("http://127.0.0.1:0");
kubeApp.MapGet("/{**path}", (string path) =>
{
    if (path.EndsWith($"/pods/session-{SessionId}"))
        return Results.Json(new
        {
            kind = "Pod", apiVersion = "v1",
            metadata = new { name = $"session-{SessionId}", @namespace = "sharing-proof" },
            status = new { phase = "Running", podIP = "127.0.0.1" }
        });
    if (!path.EndsWith("pods") && !path.EndsWith("cronjobs") && !path.EndsWith("secrets"))
        return Results.Json(new { kind = "Status", apiVersion = "v1", status = "Failure", reason = "NotFound", code = 404 }, statusCode: 404);
    return Results.Json(new
    {
        kind = path.EndsWith("cronjobs") ? "CronJobList" : path.EndsWith("secrets") ? "SecretList" : "PodList",
        apiVersion = path.Contains("batch") ? "batch/v1" : "v1",
        metadata = new { },
        items = Array.Empty<object>()
    });
});
await kubeApp.StartAsync();
var kubeUrl = kubeApp.Urls.First();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["ConnectionStrings:Postgres"] = connection;
builder.Configuration["AgentHub:Namespace"] = "sharing-proof";
builder.Configuration["AgentHub:AgentPort"] = podPort.ToString();
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
    .AddApplicationPart(typeof(SharingController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlySharing()));
builder.Services.AddAuthentication("proof").AddScheme<AuthenticationSchemeOptions, ProofAuth>("proof", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddSharingRecipientSearch();
builder.Services.AddSingleton<PermissionStore>();
builder.Services.AddSingleton<PostgresProjectStore>();
builder.Services.AddSingleton<IBrowserService>(_ => DispatchProxy.Create<IBrowserService, StoppedBrowser>());
builder.Services.AddSingleton<ISessionService>(s => new KubernetesSessionService(
    builder.Configuration, s.GetRequiredService<PostgresSessionStore>(), s.GetRequiredService<PostgresProjectStore>(),
    new NullArtifactStore(), s.GetRequiredService<IBrowserService>(), null!, null!, null!, null!, null!,
    new InMemoryEphemeralApiMcpStore(), null!, s.GetRequiredService<ILogger<KubernetesSessionService>>(),
    cluster: new Kubernetes(new KubernetesClientConfiguration { Host = kubeUrl })));
builder.Services.AddSingleton<IEnterpriseLicense, ProofLicense>();
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddSingleton<PostgresSessionStore>();
builder.Services.AddSingleton<ISessionStore>(s => s.GetRequiredService<PostgresSessionStore>());
builder.Services.AddSingleton<SessionShareStore>();
builder.Services.AddSingleton<ISessionShareStore>(s => s.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<ISessionAccessStore>(s => s.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<ISessionAccessService, SessionAccessService>();
// The pod's identity is the one thing the Kubernetes stand-in cannot answer the way a cluster
// does (it checks the caller's source address); every request still goes over real HTTP.
builder.Services.AddSingleton<IAgentPodRequests>(_ => new AgentPodRequests(new HttpClient(), new LivePod()));
builder.Services.AddSingleton<ILiveTranscriptReader>(s => new LiveTranscriptReader(
    s.GetRequiredService<IAgentPodRequests>(), s.GetRequiredService<ISessionStore>(), s.GetRequiredService<IConfiguration>()));
#if !BASELINE
builder.Services.AddSingleton<ISharedSessionHistory, SharedSessionHistory>();
#endif
builder.Services.AddSingleton<ISessionSharingService>(s => new SessionSharingService(
    s.GetRequiredService<ISessionShareStore>(), s.GetRequiredService<IEnterpriseLicense>(), "https://hub.example.com"));
var app = builder.Build();

var users = app.Services.GetRequiredService<UserDirectory>();
await users.InitializeAsync();
foreach (var user in new[] { "alice", "bob" })
    await users.RecordLoginAsync(user, $"{user}@example.com", user);
var sessions = app.Services.GetRequiredService<PostgresSessionStore>();
await sessions.InitializeAsync();
await app.Services.GetRequiredService<PostgresProjectStore>().InitializeAsync();
// Project shares read group memberships; the share store expects the table.
await new AgentHub.Api.Ee.Identity.UserGroupStore(app.Configuration).InitializeAsync();
await app.Services.GetRequiredService<PermissionStore>().InitializeAsync();
await app.Services.GetRequiredService<SessionShareStore>().InitializeAsync();
await sessions.UpsertAsync(new SessionRecord
{
    Id = SessionId, Owner = "alice", Title = "Today's reviews", Status = SessionStatus.Running,
    Mode = SessionMode.Interactive, UiMode = SessionUiMode.Terminal, CallbackToken = CallbackToken,
    McpConfigJson = "{\"mcpServers\":{\"tracker\":{\"type\":\"http\",\"url\":\"https://tracker.example.com/mcp\",\"headers\":{\"Authorization\":\"Bearer " + McpCredential + "\"}}}}"
});
// What the pod's callback stores: the capped tail (InternalController.Transcript).
await sessions.SetTranscriptAsync(SessionId, NativeTranscript.TrimToLineCap(transcript.ToString(), ScrollbackLimits.MaxChars));

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/api/config", () => new { authMode = "insecureDev" });
app.MapGet("/api/projects", () => Array.Empty<object>());
app.MapGet("/api/sessions/order", () => Array.Empty<string>());
app.MapGet("/api/admin/access", () => new { isAdmin = false });
// Proof-only: what the live path sees, the stored copy as it is, and link expiry, which the API cannot set in the past.
app.MapGet("/api/proof/live", async (ISessionService svc, ILiveTranscriptReader live) =>
{
    var info = await svc.GetSessionAsync("alice", SessionId);
    var entries = info is null ? null : await live.ReadAsync(info, default);
    return new { phase = info?.Phase, podIp = info?.PodIp, liveEntries = entries?.Count, podPort };
});
app.MapGet("/api/proof/stored-has-first", async () =>
    (await sessions.GetTranscriptAsync(SessionId))?.Contains("Review 1 of today") == true);
app.MapPost("/api/proof/expire/{linkId}", async (string linkId) =>
{
    await using var db = new Npgsql.NpgsqlConnection(connection);
    await db.OpenAsync();
    await using var cmd = new Npgsql.NpgsqlCommand(
        "UPDATE session_share_links SET expires_at = now() - interval '1 minute' WHERE id = @id", db);
    cmd.Parameters.AddWithValue("id", linkId);
    return await cmd.ExecuteNonQueryAsync();
});
app.Run(Environment.GetEnvironmentVariable("PROOF_URL") ?? "http://127.0.0.1:8080");

sealed class ProofAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Cookies["proof-user"];
        if (user is null) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim("preferred_username", user)], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}

sealed class ProofLicense : IEnterpriseLicense
{
    public LicenseStatus Status => new() { Present = true, Valid = true, Plan = "proof", Seats = 3 };
    public bool Enabled => true;
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
}

sealed class LivePod : IAgentPodIdentityResolver
{
    public Task<bool> IsLiveSessionPodAsync(string sessionId, System.Net.IPAddress ip, CancellationToken ct = default)
        => Task.FromResult(sessionId == "reviews" && System.Net.IPAddress.IsLoopback(ip));
}

sealed class OnlySharing : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in feature.Controllers.Where(controller =>
            controller.AsType() != typeof(SharingController) && controller.AsType() != typeof(SharedSessionsController)
                && controller.AsType() != typeof(SessionsController)).ToArray())
            feature.Controllers.Remove(controller);
    }
}

public class StoppedBrowser : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "GetSummaryAsync" => Task.FromResult(BrowserSummary.Stopped),
        "GetSummariesAsync" => Task.FromResult<IReadOnlyDictionary<string, BrowserSummary>>(
            ((IReadOnlyCollection<string>)args![0]!).ToDictionary(id => id, _ => BrowserSummary.Stopped)),
        _ => throw new NotSupportedException(method?.Name)
    };
}
