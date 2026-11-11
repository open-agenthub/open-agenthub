// Proof host for #495: Collaborator input in a legacy chat-mode session. The production
// SessionsController, SharingController and SharedSessionsController, the real SessionAccessService,
// SessionSharingService, KubernetesSessionService, and the terminal WebSocket route exactly as
// Program.cs maps it (ResolveUserAsync, then TerminalProxy with CanWriteTerminal), over the real
// Postgres session, user and share stores. The stand-ins: a cookie that says who is signed in, and a
// Kubernetes API that reports one running pod at 127.0.0.1. The "pod" is real: pod.mjs runs the
// branch's common/server.js with the pinned Claude Code against a mock model on PROOF_AGENT_PORT.
using System.Reflection;
using System.Security.Claims;
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
using AgentHub.Api.WebSockets;
using k8s;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;

var connection = Environment.GetEnvironmentVariable("PROOF_POSTGRES")
    ?? "Host=127.0.0.1;Port=54329;Username=postgres;Database=proof495";
const string SessionId = "pairing";
const string CallbackToken = "callback-token-pairing";
var podPort = int.Parse(Environment.GetEnvironmentVariable("PROOF_AGENT_PORT") ?? "17681");
var uiMode = Environment.GetEnvironmentVariable("PROOF_UI_MODE") ?? SessionUiMode.Chat;

// An API server that knows one running pod and nothing else.
var pod = new
{
    kind = "Pod", apiVersion = "v1",
    metadata = new
    {
        name = $"session-{SessionId}", @namespace = "sharing-proof",
        labels = new Dictionary<string, string> { ["agenthub.dev/session"] = SessionId }
    },
    status = new { phase = "Running", podIP = "127.0.0.1" }
};
var kube = WebApplication.CreateBuilder();
var kubeApp = kube.Build();
kubeApp.Urls.Add("http://127.0.0.1:0");
kubeApp.MapGet("/{**path}", (HttpContext ctx, string path) =>
{
    if (path.EndsWith($"/pods/session-{SessionId}"))
        return Results.Json(pod);
    if (path.EndsWith("/pods") && ctx.Request.Query["labelSelector"].ToString().Contains("agenthub.dev/owner=u-2bd806c97f0e00af"))
        return Results.Json(new { kind = "PodList", apiVersion = "v1", items = new[] { pod } });
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
builder.Services.AddSingleton<PostgresSessionMessageStore>();
builder.Services.AddSingleton<ISessionMessageStore>(s => s.GetRequiredService<PostgresSessionMessageStore>());
builder.Services.AddSingleton<ISessionMcpPolicyReader>(s => s.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<ILibraryAccess>(_ => DispatchProxy.Create<ILibraryAccess, StoppedBrowser>());
builder.Services.AddSingleton<ExternalShareLinkService>();
builder.Services.AddSingleton<ISessionMessageDelivery, SessionMessageDelivery>();
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
builder.Services.AddSingleton<ISharedSessionHistory, SharedSessionHistory>();
builder.Services.AddSingleton<ISessionSharingService>(s => new SessionSharingService(
    s.GetRequiredService<ISessionShareStore>(), s.GetRequiredService<IEnterpriseLicense>(), "https://hub.example.com"));
var app = builder.Build();

var users = app.Services.GetRequiredService<UserDirectory>();
await users.InitializeAsync();
foreach (var user in new[] { "alice", "bob", "carol" })
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
    Id = SessionId, Owner = "alice", Title = "Pairing", Status = SessionStatus.Running, Agent = AgentKind.Claude,
    Mode = SessionMode.Interactive, UiMode = uiMode, CallbackToken = CallbackToken, ProjectId = "fleet"
});

await app.Services.GetRequiredService<PostgresSessionMessageStore>().InitializeAsync();
await sessions.UpsertAsync(new SessionRecord
{
    Id = "sender", Owner = "bob", Title = "Sender", CallbackToken = "callback-token-sender"
});
await sessions.UpsertAsync(new SessionRecord
{
    Id = "viewer", Owner = "carol", Title = "Viewer", CallbackToken = "callback-token-viewer"
});
await sessions.UpsertAsync(new SessionRecord
{
    Id = "owned", Owner = "alice", Title = "Owned Sender", CallbackToken = "callback-token-owned", ProjectId = "fleet"
});

app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
// Peek without consuming the inbox so the proof can distinguish queued and delivered rows.
app.MapGet("/proof/messages", (PostgresSessionMessageStore messages, CancellationToken ct) =>
    messages.ListPageAsync("alice", SessionId, null, 20, ct));
app.MapGet("/api/config", () => new { authMode = "insecureDev" });
app.MapGet("/api/projects", () => Array.Empty<object>());
app.MapGet("/api/sessions/order", () => Array.Empty<string>());
app.MapGet("/api/admin/access", () => new { isAdmin = false });
// Program.cs's /ws/sessions/{id}/terminal: ProxySharedWs -> ProxyResolvedWs, copied because they
// are local functions of the production Program.
app.Map("/ws/sessions/{id}/terminal", async (HttpContext ctx, string id, ISessionAccessService access,
    ISessionService svc, IAgentPodRequests pods, ILoggerFactory lf) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    var principal = ctx.User.FindFirstValue("preferred_username");
    if (principal is null) { ctx.Response.StatusCode = 401; return; }
    var resolved = await access.ResolveUserAsync(principal, id, ctx.RequestAborted);
    if (resolved is null) { ctx.Response.StatusCode = 404; return; }
#if BASELINE
    // Before ReadForUpgradeAsync existed, Program.cs read the live session directly.
    var live = await svc.GetSessionAsync(resolved.Session.Owner, resolved.Session.Id, ctx.RequestAborted);
    if (live is null) { ctx.Response.StatusCode = 404; return; }
#else
    var live = await TerminalProxy.ReadForUpgradeAsync(ctx,
        ct => svc.GetSessionAsync(resolved.Session.Owner, resolved.Session.Id, ct), lf.CreateLogger("TerminalProxy"));
    if (live is null) return;
#endif
    await TerminalProxy.HandleAsync(ctx, live, SessionAccessRules.CanWriteTerminal(resolved.Level), pods, lf, podPort,
        onActivity: ct => svc.TouchActivityAsync(resolved.Session.Owner, resolved.Session.Id, ct));
}).RequireAuthorization();
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
        => Task.FromResult(sessionId == "pairing" && System.Net.IPAddress.IsLoopback(ip));
}

sealed class OnlySharing : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in feature.Controllers.Where(controller =>
            controller.AsType() != typeof(SharingController) && controller.AsType() != typeof(SharedSessionsController)
                && controller.AsType() != typeof(SessionsController)
                && controller.AsType() != typeof(InternalController)
                && controller.AsType() != typeof(RemoteShareLinkController)).ToArray())
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
