// Proof host for the Workspace model switch (#478): the REAL SessionModelsController (catalog read
// and switch), the real SessionModelSwitch and SessionModelCatalogs, the real Postgres stores
// (sessions, shares, model catalogs) and the real SessionAccessService. Kubernetes is replaced
// only where it answers "is this pod running and where": the session agent runs on
// 127.0.0.1:AGENT_PORT. The signed-in user is switched with POST /__as/{user}.
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
b.Services.AddControllers().AddApplicationPart(typeof(SessionModelsController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new Only())).AddControllersAsServices();
b.Services.AddAuthentication("t").AddScheme<AuthenticationSchemeOptions, As>("t", _ => { });
b.Services.AddAuthorization();
b.Services.AddSingleton<ISessionStore, PostgresSessionStore>();
b.Services.AddSingleton<UserDirectory>();
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
b.Services.AddTransient<ISessionModelSwitch, SessionModelSwitch>();
var app = b.Build();
foreach (var init in new Func<Task>[] {
    () => app.Services.GetRequiredService<ISessionStore>().InitializeAsync(),
    () => app.Services.GetRequiredService<UserDirectory>().InitializeAsync(),
    () => app.Services.GetRequiredService<PostgresProjectStore>().InitializeAsync(),
    () => app.Services.GetRequiredService<AgentHub.Api.Ee.Identity.UserGroupStore>().InitializeAsync(),
    () => app.Services.GetRequiredService<SessionShareStore>().InitializeAsync(),
    () => app.Services.GetRequiredService<IModelCatalogStore>().InitializeAsync() })
    await init();
app.UseAuthentication(); app.UseAuthorization();
app.MapPost("/__as/{user}", (string user) => { As.User = user; return Results.Ok(new { user }); }).AllowAnonymous();
// A running Codex session of alice's on an API key, shared with bob as viewer and carol as collaborator.
app.MapPost("/__seed", async (ISessionStore store, UserDirectory users, SessionShareStore shares, string token) =>
{
    foreach (var user in new[] { "alice", "bob", "carol" }) await users.RecordLoginAsync(user, $"{user}@example.com", user);
    var record = new SessionRecord { Id = "proof-session", Owner = "alice", Title = "Model switch proof", Mode = SessionMode.Interactive,
        Agent = AgentKind.Codex, AuthMode = AgentAuthMode.ApiKey, CallbackToken = token, Status = "Running" };
    await store.UpsertAsync(record);
    await shares.UpsertDirectAsync("alice", record.Id, "bob", ShareRole.Viewer);
    await shares.UpsertDirectAsync("alice", record.Id, "carol", ShareRole.Collaborator);
    return Results.Ok();
}).AllowAnonymous();
// What the session row says now, for the stand-in hub's /api/sessions answer.
app.MapGet("/__session", async (ISessionStore store) =>
    Results.Json(new { modelName = (await store.GetByIdAsync("proof-session"))?.ModelName })).AllowAnonymous();
app.MapControllers();
app.Run("http://127.0.0.1:" + (Environment.GetEnvironmentVariable("HOST_PORT") ?? "8081"));

sealed class Only : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in feature.Controllers.ToList())
            if (controller.AsType() != typeof(SessionModelsController)) feature.Controllers.Remove(controller);
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
