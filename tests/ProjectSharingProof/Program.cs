// Proof host for #409: the production SessionsController, ProjectsController, SharingController,
// ProjectSharingController and SharedSessionsController, the real SessionAccessService,
// SessionSharingService and ProjectSharingService, and the real Postgres session, user, group,
// project and share stores. The stand-ins: a cookie that says who is signed in, a stopped browser,
// a Kubernetes API that lists no pods (so each session's phase is its stored status), and
// POST /api/proof/sessions, which writes a session row the way session creation does, without a pod.
using System.Net;
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
using k8s;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;

var connection = Environment.GetEnvironmentVariable("PROOF_POSTGRES")
    ?? "Host=127.0.0.1;Port=54329;Username=postgres;Database=proof409";

// An API server that has no pods, cron jobs or secrets. KubernetesSessionService only lists here.
var kube = WebApplication.CreateBuilder();
var kubeApp = kube.Build();
kubeApp.MapGet("/{**path}", (string path) => Results.Json(new
{
    kind = path.EndsWith("cronjobs") ? "CronJobList" : path.EndsWith("secrets") ? "SecretList" : "PodList",
    apiVersion = path.Contains("batch") ? "batch/v1" : "v1",
    metadata = new { },
    items = Array.Empty<object>()
}));
await kubeApp.StartAsync();
var kubeUrl = kubeApp.Urls.First();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["ConnectionStrings:Postgres"] = connection;
builder.Configuration["AgentHub:Namespace"] = "sharing-proof";
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
    .AddApplicationPart(typeof(SharingController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlySharing()));
builder.Services.AddAuthentication("proof").AddScheme<AuthenticationSchemeOptions, ProofAuth>("proof", _ => { });
builder.Services.AddAuthorization();
builder.Services.AddSharingRecipientSearch();
builder.Services.AddSingleton<PermissionStore>();
builder.Services.AddSingleton<PostgresProjectStore>();
builder.Services.AddSingleton<IProjectStore>(s => s.GetRequiredService<PostgresProjectStore>());
builder.Services.AddSingleton<AgentHub.Api.Ee.Identity.UserGroupStore>();
builder.Services.AddSingleton<IBrowserService>(_ => DispatchProxy.Create<IBrowserService, StoppedBrowser>());
builder.Services.AddSingleton<ISessionService>(s => new KubernetesSessionService(
    builder.Configuration, s.GetRequiredService<PostgresSessionStore>(), s.GetRequiredService<PostgresProjectStore>(),
    new NullArtifactStore(), s.GetRequiredService<IBrowserService>(), null!, null!, null!, null!, null!,
    new InMemoryEphemeralApiMcpStore(), null!, s.GetRequiredService<ILogger<KubernetesSessionService>>(),
    cluster: new Kubernetes(new KubernetesClientConfiguration { Host = kubeUrl })));
builder.Services.AddSingleton<IEnterpriseLicense, ProofLicense>();
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddSingleton<PostgresSessionStore>();
builder.Services.AddSingleton<SessionShareStore>();
builder.Services.AddSingleton<ISessionShareStore>(s => s.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<ISessionAccessStore>(s => s.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<IProjectShareStore>(s => s.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<IProjectSharingService, ProjectSharingService>();
builder.Services.AddSingleton<ISessionAccessService, SessionAccessService>();
builder.Services.AddSingleton<ISessionSharingService>(s => new SessionSharingService(
    s.GetRequiredService<ISessionShareStore>(), s.GetRequiredService<IEnterpriseLicense>(), "https://hub.example.com"));
var app = builder.Build();

var users = app.Services.GetRequiredService<UserDirectory>();
await users.InitializeAsync();
foreach (var user in new[] { "alice", "bob", "carol", "dave" })
    await users.RecordLoginAsync(user, $"{user}@example.com", user);
var sessions = app.Services.GetRequiredService<PostgresSessionStore>();
await sessions.InitializeAsync();
await app.Services.GetRequiredService<PostgresProjectStore>().InitializeAsync();
await app.Services.GetRequiredService<PermissionStore>().InitializeAsync();
var groups = app.Services.GetRequiredService<AgentHub.Api.Ee.Identity.UserGroupStore>();
await groups.InitializeAsync();
await app.Services.GetRequiredService<SessionShareStore>().InitializeAsync();
// What the IdP would report at sign-in: alice and carol are in release-team, dave is not.
await groups.ReplaceGroupsAsync("alice", ["release-team"]);
await groups.ReplaceGroupsAsync("carol", ["release-team"]);
await groups.ReplaceGroupsAsync("dave", ["other-team"]);

// alice's project "Release" with a parent session, a child started outside the project and a
// second session; one session outside any project; carol's session that names alice's project id.
var projects = app.Services.GetRequiredService<PostgresProjectStore>();
var release = await projects.CreateAsync("alice", new CreateProjectRequest("Release", "#d08a3c"));
var seeded = DateTime.UtcNow;
async Task Seed(string id, string owner, string title, string status, string? project, string? parent = null, int age = 0)
    => await sessions.UpsertAsync(new SessionRecord
    {
        Id = id, Owner = owner, Title = title, Status = status, Mode = SessionMode.Interactive,
        CallbackToken = $"callback-{id}", ProjectId = project, ParentSessionId = parent, CreatedAt = seeded.AddMinutes(-age)
    });
await Seed("prep", "alice", "Release prep", "Paused", release.Id, age: 10);
await Seed("notes", "alice", "Release notes", "Paused", null, "prep", 8);
await Seed("qa", "alice", "Release QA", "Paused", release.Id, age: 6);
await Seed("private", "alice", "Private notes", "Paused", null, age: 4);
await Seed("planted", "carol", "Carol in alice's project", "Paused", release.Id, age: 3);
await Seed("bob-own", "bob", "Bob's own work", "Paused", null, age: 20);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapGet("/api/config", () => new { authMode = "insecureDev" });
app.MapPost("/api/proof/sessions", async (HttpContext context, SessionRecordSeed seed) =>
{
    await sessions.UpsertAsync(new SessionRecord
    {
        Id = seed.Id, Owner = context.Request.Cookies["proof-user"]!, Title = seed.Title, Status = "Paused",
        Mode = SessionMode.Interactive, CallbackToken = $"callback-{seed.Id}", ProjectId = seed.ProjectId,
        CreatedAt = DateTime.UtcNow
    });
    return Results.Ok();
});
app.MapGet("/api/proof/release", () => new { id = release.Id });
app.MapGet("/api/sessions/order", () => Array.Empty<string>());
app.MapGet("/api/admin/access", () => new { isAdmin = false });
app.Run(Environment.GetEnvironmentVariable("PROOF_URL") ?? "http://0.0.0.0:8080");

sealed record SessionRecordSeed(string Id, string Title, string? ProjectId);

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

sealed class OnlySharing : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in feature.Controllers.Where(controller =>
            controller.AsType() != typeof(SharingController) && controller.AsType() != typeof(SharedSessionsController)
                && controller.AsType() != typeof(SessionsController) && controller.AsType() != typeof(ProjectsController)
                && controller.AsType() != typeof(ProjectSharingController)).ToArray())
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
