// Usage proof host (#258, #282): the REAL UsageController, OtelController and InternalController with the REAL
// Postgres session and usage stores, behind an auth handler whose user is switched by
// POST /__as/{user}. Real CLIs report into it: Claude Code over OTLP (/internal/otel/v1/metrics),
// the session agent's common/server.js over /internal/sessions/{id}/usage. The frontend's Vite
// dev server proxies /api here, so the Usage page in Chromium reads what the CLIs reported.
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Usage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;

var b = WebApplication.CreateBuilder(args);
b.Services.AddControllers()
    .AddApplicationPart(typeof(UsageController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new Only(
        typeof(UsageController), typeof(OtelController), typeof(InternalController))));
b.Services.AddAuthentication("t").AddScheme<AuthenticationSchemeOptions, As>("t", _ => { });
b.Services.AddAuthorization();
b.Services.AddSingleton<PostgresSessionStore>();
b.Services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<PostgresSessionStore>());
b.Services.AddSingleton<PostgresUsageStore>();
b.Services.AddSingleton<IUsageStore>(sp => sp.GetRequiredService<PostgresUsageStore>());
b.Services.AddSingleton<UserDirectory>();
b.Services.AddSingleton<IPersonalUsageLimitSource>(sp => sp.GetRequiredService<UserDirectory>());
b.Services.AddSingleton<UsageLimitService>();
b.Services.AddSingleton<AgentHub.Api.Permissions.PermissionStore>();
// Dependencies the usage paths never call: a proxy that throws if one ever is.
foreach (var t in new[] { typeof(AgentHub.Api.Services.ISessionService), typeof(AgentHub.Api.Ee.Sharing.ISessionMcpPolicyReader),
                          typeof(AgentHub.Api.Library.ILibraryAccess) })
    b.Services.AddSingleton(t, Unused.Create(t));
var app = b.Build();
await app.Services.GetRequiredService<PostgresSessionStore>().InitializeAsync();
await app.Services.GetRequiredService<PostgresUsageStore>().InitializeAsync();
await app.Services.GetRequiredService<UserDirectory>().InitializeAsync();
app.UseAuthentication(); app.UseAuthorization();
app.MapPost("/__as/{user}", (string user) => { As.User.Value = user; return Results.Ok(new { user }); }).AllowAnonymous();
// Sessions as the hub would have created them: id, owner, agent, auth mode, callback token, and
// the billing a start stores (KubernetesSessionService.BuildPodContextAsync, which needs a cluster).
app.MapPost("/__seed", async (SeedBody s, ISessionStore store) => {
    await store.UpsertAsync(new SessionRecord { Id = s.Id, Owner = s.Owner, Title = s.Title, Agent = s.Agent,
        AuthMode = s.AuthMode, Mode = s.Mode, CallbackToken = s.Token });
    if (s.UsageBilling is not null) await store.SetUsageBillingAsync(s.Id, s.UsageBilling);
    return Results.Ok(); }).AllowAnonymous();
// The start gate as KubernetesSessionService calls it (minus the cluster): the real
// UsageLimitService against the spend the real stores recorded. 409 = refused over budget.
app.MapPost("/__can-start", async (CanStartBody c, UsageLimitService limits) => {
    try
    {
        await limits.EnsureCanStartAsync(c.Owner, c.Agent, UsageBilling.ForPod(c.AuthMode, c.Mode, c.HasApiKey, c.HasSubscription));
        return Results.Ok(new { allowed = true });
    }
    catch (UsageLimitExceededException e) { return Results.Json(new { allowed = false, e.Message }, statusCode: 409); }
}).AllowAnonymous();
app.MapGet("/api/config", () => Results.Json(new { authority = "" })).AllowAnonymous();
app.MapGet("/api/sessions", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/credentials", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/agents/allowed", () => Results.Json(new { agents = Array.Empty<string>() })).AllowAnonymous();
app.MapControllers();
app.Run(Environment.GetEnvironmentVariable("HOST_URL") ?? "http://127.0.0.1:8080");

record CanStartBody(string Owner, AgentKind Agent, AgentAuthMode AuthMode, SessionMode Mode, bool HasApiKey = false, bool HasSubscription = false);
record SeedBody(string Id, string Owner, string Title, AgentKind Agent, AgentAuthMode AuthMode, SessionMode Mode, string Token, string? UsageBilling = null);

sealed class Only(params Type[] types) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var c in feature.Controllers.Where(c => !types.Contains(c.AsType())).ToList())
            feature.Controllers.Remove(c);
    }
}

sealed class As(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    // A box rather than a mutable static field: the user is switched at run time by POST /__as/{user}.
    public static readonly System.Runtime.CompilerServices.StrongBox<string> User = new("alice");
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("preferred_username", User.Value!) }, "t")), "t")));
}

class Unused : DispatchProxy
{
    public static object Create(Type t) =>
        typeof(DispatchProxy).GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)!
            .MakeGenericMethod(t, typeof(Unused)).Invoke(null, null)!;
    protected override object? Invoke(MethodInfo? m, object?[]? a) => throw new NotSupportedException(m?.Name);
}
