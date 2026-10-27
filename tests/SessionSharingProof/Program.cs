using System.Security.Claims;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Net;
using AgentHub.Api.Controllers;
using AgentHub.Api.Services;
using AgentHub.Api.Browser;
using AgentHub.Api.Library;
using AgentHub.Api.Storage;
using AgentHub.Api.Permissions;
using k8s;
using System.Text.Encodings.Web;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Licensing;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=54329;Username=postgres;Database=proof328";
builder.Configuration["AgentHub:Namespace"] = "sharing-proof";
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter())).AddApplicationPart(typeof(SharingController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlySharing()));
var issuer = Environment.GetEnvironmentVariable("PROOF_ISSUER");
builder.Services.AddAuthentication("select")
    .AddPolicyScheme("select", "select", o => o.ForwardDefaultSelector = context =>
        context.Request.Headers.Authorization.ToString().StartsWith("Bearer ") ? "jwt" : "proof")
    .AddScheme<AuthenticationSchemeOptions, ProofAuth>("proof", _ => {})
    .AddJwtBearer("jwt", o => { o.Authority = issuer; o.Audience = "proof328"; o.RequireHttpsMetadata = false; o.MapInboundClaims = false; });
builder.Services.AddAuthorization();
builder.Services.AddSharingRecipientSearch();
builder.Services.AddSingleton<PermissionStore>();
builder.Services.AddSingleton<PostgresProjectStore>();
builder.Services.AddSingleton<IBrowserService>(_ => DispatchProxy.Create<IBrowserService, StoppedBrowser>());
builder.Services.AddSingleton<ISessionService>(s => new KubernetesSessionService(
    builder.Configuration, s.GetRequiredService<PostgresSessionStore>(), s.GetRequiredService<PostgresProjectStore>(),
    new NullArtifactStore(), s.GetRequiredService<IBrowserService>(), null!, null!, null!, null!, null!,
    new InMemoryEphemeralApiMcpStore(), null!, s.GetRequiredService<ILogger<KubernetesSessionService>>(),
    cluster: new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(Environment.GetEnvironmentVariable("PROOF_KUBECONFIG")))));
builder.Services.AddSingleton<IEnterpriseLicense, ProofLicense>();
builder.Services.AddSingleton<UserDirectory>();
builder.Services.AddSingleton<PostgresSessionStore>();
builder.Services.AddSingleton<SessionShareStore>();
builder.Services.AddSingleton<ISessionShareStore>(s => s.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<ISessionAccessStore>(s => s.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<ISessionAccessService, SessionAccessService>();
builder.Services.AddSingleton<ISessionSharingService>(s => new SessionSharingService(s.GetRequiredService<ISessionShareStore>(), s.GetRequiredService<IEnterpriseLicense>(), null));
var app = builder.Build();
var users = app.Services.GetRequiredService<UserDirectory>();
await users.InitializeAsync();
await users.RecordLoginAsync("alice", "alice@example.com", "Alice");
await users.RecordLoginAsync("bob", "bob@example.com", "Bob");
var sessions = app.Services.GetRequiredService<PostgresSessionStore>();
await sessions.InitializeAsync();
await app.Services.GetRequiredService<PostgresProjectStore>().InitializeAsync();
await app.Services.GetRequiredService<PermissionStore>().InitializeAsync();
await sessions.UpsertAsync(new SessionRecord { Id = "alice-session", Owner = "alice", CallbackToken = "proof-a", Title = "Sharing proof", Status = "Paused" });
await sessions.UpsertAsync(new SessionRecord { Id = "bob-session", Owner = "bob", CallbackToken = "proof-b", Title = "Foreign session", Status = "Paused" });
await sessions.SetScrollbackAsync("alice-session", "Shared proof transcript");
await app.Services.GetRequiredService<SessionShareStore>().InitializeAsync();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapControllers();
app.MapPost("/__as/{user}", (string user, HttpContext ctx) => { ctx.Response.Cookies.Append("proof-user", user); return Results.Ok(); });
app.MapGet("/api/config", () => new { authMode = issuer is null ? "insecureDev" : "oidc", authority = issuer, clientId = "proof328", scope = "openid profile" });

app.MapGet("/api/projects", () => Array.Empty<object>());
app.MapGet("/api/sessions/order", () => Array.Empty<string>());
app.MapGet("/api/admin/access", () => new { isAdmin = false });
app.Run("http://0.0.0.0:8080");

sealed class ProofAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Cookies["proof-user"];
        if (user is null) return Task.FromResult(AuthenticateResult.NoResult());
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], Scheme.Name)), Scheme.Name)));
    }
}
sealed class ProofLicense : IEnterpriseLicense
{
    public LicenseStatus Status => new() { Present = true, Valid = true, Plan = "proof", Seats = 2 };
    public bool Enabled => true;
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
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
