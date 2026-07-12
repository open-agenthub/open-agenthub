using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentHub.Api.Services;
using AgentHub.Api.WebSockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// (De)serialize enums as strings – the frontend sends e.g. mode: "Interactive".
builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton<ISessionService, KubernetesSessionService>();
builder.Services.AddSingleton<AgentHub.Api.Persistence.ISessionStore, AgentHub.Api.Persistence.PostgresSessionStore>();
builder.Services.AddSingleton<AgentHub.Api.Persistence.ApiTokenStore>();
// S3 is optional: without an access key the platform runs without state/artifact persistence (no resume).
if (!string.IsNullOrWhiteSpace(builder.Configuration["S3:AccessKey"]))
    builder.Services.AddSingleton<AgentHub.Api.Storage.IArtifactStore, AgentHub.Api.Storage.S3ArtifactStore>();
else
    builder.Services.AddSingleton<AgentHub.Api.Storage.IArtifactStore, AgentHub.Api.Storage.NullArtifactStore>();
builder.Services.AddHttpClient<AgentHub.Api.Notifications.INotifier, AgentHub.Api.Notifications.N8nNotifier>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IGitAuthService, GitAuthService>();

// Enterprise license gate (offline-verified token).
builder.Services.AddSingleton<AgentHub.Api.Licensing.IEnterpriseLicense, AgentHub.Api.Licensing.EnterpriseLicense>();

// Enterprise: Slack integration (only active with a valid license + tokens).
var slackOpts = builder.Configuration.GetSection("Ee:Slack").Get<AgentHub.Api.Ee.Slack.SlackOptions>() ?? new();
builder.Services.AddSingleton(slackOpts);
builder.Services.AddSingleton<AgentHub.Api.Ee.Slack.SlackThreadStore>();
builder.Services.AddSingleton<AgentHub.Api.Ee.Slack.SlackClient>();
builder.Services.AddSingleton<AgentHub.Api.Notifications.INotifier, AgentHub.Api.Ee.Slack.SlackNotifier>();
builder.Services.AddHostedService<AgentHub.Api.Ee.Slack.SlackSocketModeService>();

builder.Services.AddHealthChecks();

// --- Auth: generic OIDC/JWT provider (e.g. Keycloak). Multi-user separation via preferred_username. ---
// Without an authority the backend runs in "auth disabled" mode (local development): every request = user "dev".
var oidc = builder.Configuration.GetSection("Oidc");
var authEnabled = !string.IsNullOrWhiteSpace(oidc["Authority"]);
if (authEnabled)
{
    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(o =>
        {
            o.Authority = oidc["Authority"];
            // Empty audience = skip audience validation. Providers like Keycloak
            // do not put the client id into "aud" unless an audience mapper is
            // configured on the client.
            if (string.IsNullOrWhiteSpace(oidc["Audience"]))
                o.TokenValidationParameters.ValidateAudience = false;
            else
                o.Audience = oidc["Audience"];
            o.RequireHttpsMetadata = oidc.GetValue("RequireHttpsMetadata", true);
            // WebSocket: the token may also arrive as a query parameter, since browser WebSockets cannot set headers.
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = ctx =>
                {
                    if (string.IsNullOrEmpty(ctx.Token) &&
                        ctx.Request.Path.StartsWithSegments("/ws") &&
                        ctx.Request.Query.TryGetValue("access_token", out var t))
                        ctx.Token = t;
                    return Task.CompletedTask;
                }
            };
        });
}
else
{
    builder.Services
        .AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);
}
builder.Services.AddAuthorization();

// CORS only for our own frontend (origin can be overridden via config).
var frontendOrigin = builder.Configuration["FrontendOrigin"] ?? "http://localhost:5173";
builder.Services.AddCors(c => c.AddDefaultPolicy(p =>
    p.WithOrigins(frontendOrigin).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// Create the Postgres schema idempotently.
using (var scope = app.Services.CreateScope())
{
    var store = scope.ServiceProvider.GetRequiredService<AgentHub.Api.Persistence.ISessionStore>();
    await store.InitializeAsync();
    var tokenStore = scope.ServiceProvider.GetRequiredService<AgentHub.Api.Persistence.ApiTokenStore>();
    await tokenStore.InitializeAsync();
    await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Ee.Slack.SlackThreadStore>().InitializeAsync();
}

app.UseCors();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/healthz").AllowAnonymous();

// Runtime config for the frontend (static nginx image, no build-time env vars):
// empty authority = auth disabled, so the frontend does not enforce a login.
app.MapGet("/api/config", (IGitAuthService git) => Results.Ok(new
{
    authority = oidc["Authority"] ?? "",
    clientId = oidc["ClientId"] ?? "agenthub",
    scope = oidc["Scope"] ?? "openid profile email",
    // Lets the UI show the "Connect GitHub/GitLab" account section only when configured.
    gitEnabled = git.AnyConfigured
})).AllowAnonymous();

var agentPort = builder.Configuration.GetValue("AgentHub:AgentPort", 7681);

// --- Terminal stream: browser WS -> proxy -> agent pod ---
app.Map("/ws/sessions/{id}/terminal", async (HttpContext ctx, string id,
        ISessionService sessions, ILoggerFactory lf) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    if (ctx.User.Identity?.IsAuthenticated != true) { ctx.Response.StatusCode = 401; return; }

    var owner = ctx.User.FindFirstValue("preferred_username")
                ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (owner is null) { ctx.Response.StatusCode = 401; return; }

    await TerminalProxy.HandleAsync(ctx, owner, id, sessions, lf, agentPort);
}).RequireAuthorization();

app.Run();

// Dev auth (only active without a configured Oidc__Authority): authenticates every request as "dev",
// so [Authorize] endpoints and owner separation work locally without an OIDC provider.
file sealed class DevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dev";
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim("preferred_username", "dev")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
