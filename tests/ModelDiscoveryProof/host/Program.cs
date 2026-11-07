// Proof host for model discovery (#461): the REAL ModelCatalogController, SessionModelsController
// and InternalController, the real Postgres stores (sessions, shares, model catalogs), the real
// SessionAccessService and SessionModelCatalogs. Kubernetes is replaced only where it answers
// "is this pod running and where": the session agent runs on 127.0.0.1:AGENT_PORT.
// The signed-in user is switched with POST /__as/{user}.
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var b = WebApplication.CreateBuilder(args);
b.Configuration["AgentHub:AgentPort"] = Environment.GetEnvironmentVariable("AGENT_PORT") ?? "7681";
b.Services.AddControllers().AddApplicationPart(typeof(ModelCatalogController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new Only())).AddControllersAsServices();
b.Services.AddAuthentication("t").AddScheme<AuthenticationSchemeOptions, As>("t", _ => { });
b.Services.AddAuthorization();
b.Services.AddSingleton<ISessionStore, PostgresSessionStore>();
b.Services.AddSingleton<AgentHub.Api.Persistence.UserDirectory>();
b.Services.AddSingleton<PostgresProjectStore>();
b.Services.AddSingleton<AgentHub.Api.Ee.Identity.UserGroupStore>();
b.Services.AddSingleton(sp => new SessionShareStore(sp.GetRequiredService<IConfiguration>(), NullLogger<SessionShareStore>.Instance));
b.Services.AddSingleton<ISessionAccessStore>(sp => sp.GetRequiredService<SessionShareStore>());
b.Services.AddSingleton<IEnterpriseLicense>(new Lic());
b.Services.AddSingleton<ISessionAccessService, SessionAccessService>();
b.Services.AddSingleton<IModelCatalogStore, PostgresModelCatalogStore>();
b.Services.AddSingleton<ISessionService>(_ => DispatchProxy.Create<ISessionService, LivePod>());
b.Services.AddSingleton<IAgentPodRequests>(_ => new AgentPodRequests(new HttpClient(), new Loopback()));
b.Services.AddTransient<ISessionModelCatalogs, SessionModelCatalogs>();
// The real controller; dependencies the /models action never touches are left out, as the
// backend's own tests construct it.
b.Services.AddTransient(sp => new InternalController(sp.GetRequiredService<ISessionStore>(), [], sp.GetRequiredService<ISessionService>(),
    null!, [], [], null!, null!, modelCatalogs: sp.GetRequiredService<ISessionModelCatalogs>()));
var app = b.Build();
foreach (var init in new Func<Task>[] {
    () => app.Services.GetRequiredService<ISessionStore>().InitializeAsync(),
    () => app.Services.GetRequiredService<AgentHub.Api.Persistence.UserDirectory>().InitializeAsync(),
    () => app.Services.GetRequiredService<PostgresProjectStore>().InitializeAsync(),
    () => app.Services.GetRequiredService<AgentHub.Api.Ee.Identity.UserGroupStore>().InitializeAsync(),
    () => app.Services.GetRequiredService<SessionShareStore>().InitializeAsync(),
    () => app.Services.GetRequiredService<IModelCatalogStore>().InitializeAsync() })
    await init();
app.UseAuthentication(); app.UseAuthorization();
app.MapPost("/__as/{user}", (string user) => { As.User = user; return Results.Ok(new { user }); }).AllowAnonymous();
// Seeds a running Codex session of alice's on acct-1, shared with bob as viewer.
app.MapPost("/__seed", async (ISessionStore store, AgentHub.Api.Persistence.UserDirectory users, SessionShareStore shares, string token) =>
{
    foreach (var user in new[] { "alice", "bob" }) await users.RecordLoginAsync(user, $"{user}@example.com", user);
    var record = new SessionRecord { Id = "proof-session", Owner = "alice", Title = "Proof", Mode = SessionMode.Interactive,
        Agent = AgentKind.Codex, AuthMode = AgentAuthMode.Subscription, CredentialId = "acct-1", ModelName = "gpt-6-luna",
        CallbackToken = token, Status = "Running" };
    await store.UpsertAsync(record);
    await store.SetResolvedCredentialIdAsync(record.Id, "acct-1");
    await shares.UpsertDirectAsync("alice", record.Id, "bob", ShareRole.Viewer);
    return Results.Ok();
}).AllowAnonymous();
// What SwitchSessionCredentialAsync writes before it pushes the login to the pod.
app.MapPost("/__switch/{account}", async (ISessionStore store, string account) =>
{
    await store.SetCredentialIdAsync("proof-session", account);
    await store.SetResolvedCredentialIdAsync("proof-session", account);
    return Results.Ok();
}).AllowAnonymous();
app.MapGet("/api/config", () => Results.Json(new { authority = "" })).AllowAnonymous();
app.MapGet("/api/sessions", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/sessions/shared", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/projects", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/credentials", () => Results.Json(new { codexSubscription = true, codexApiKey = true })).AllowAnonymous();
app.MapGet("/api/credentials/accounts", () => Results.Json(new Dictionary<string, object[]> {
    ["Claude"] = [], ["Cursor"] = [], ["OpenClaw"] = [], ["OpenCode"] = [],
    ["Codex"] = [ new { id = "acct-1", label = "Work", isDefault = true }, new { id = "acct-2", label = "Private", isDefault = false },
                  new { id = "acct-deny", label = "Expired plan", isDefault = false } ] })).AllowAnonymous();
app.MapGet("/api/agents/allowed", () => Results.Json(new { agents = new[] { "Claude", "Codex", "Cursor", "OpenClaw", "OpenCode" } })).AllowAnonymous();
app.MapGet("/api/secrets", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/mcp-servers", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/git/providers", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/session-organization/order", () => Results.Json(new { sessionIds = Array.Empty<string>() })).AllowAnonymous();
app.MapControllers();
app.Run("http://127.0.0.1:8080");

sealed class Only : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        var keep = new[] { typeof(ModelCatalogController), typeof(SessionModelsController), typeof(InternalController) };
        foreach (var controller in feature.Controllers.Where(controller => !keep.Contains(controller.AsType())).ToList())
            feature.Controllers.Remove(controller);
    }
}

public class LivePod : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == nameof(ISessionService.GetSessionAsync)
        ? Task.FromResult<SessionInfo?>(new SessionInfo { Id = (string)args![1]!, Owner = (string)args[0]!, Title = "Proof",
            Mode = SessionMode.Interactive, Phase = "Running", PodIp = "127.0.0.1" })
        : throw new NotSupportedException(method.Name);
}

sealed class Loopback : IAgentPodIdentityResolver
{
    public Task<bool> IsLiveSessionPodAsync(string sessionId, IPAddress sourceIp, CancellationToken ct = default) =>
        Task.FromResult(IPAddress.IsLoopback(sourceIp));
}

sealed class As(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    public static volatile string User = "alice";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("preferred_username", User) }, "t")), "t")));
}

sealed class Lic : IEnterpriseLicense
{
    public LicenseStatus Status => new() { Valid = true, Present = true, Plan = "test", Seats = 10 };
    public bool Enabled => true;
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
}
