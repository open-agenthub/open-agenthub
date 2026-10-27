using System.Security.Claims;
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
builder.Services.AddControllers().AddApplicationPart(typeof(SharingController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlySharing()));
builder.Services.AddAuthentication("proof").AddScheme<AuthenticationSchemeOptions, ProofAuth>("proof", _ => {});
builder.Services.AddAuthorization();
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
await sessions.UpsertAsync(new SessionRecord { Id = "alice-session", Owner = "alice", CallbackToken = "proof-a", Title = "Sharing proof", Status = "Paused" });
await sessions.UpsertAsync(new SessionRecord { Id = "bob-session", Owner = "bob", CallbackToken = "proof-b", Title = "Foreign session", Status = "Paused" });
await app.Services.GetRequiredService<SessionShareStore>().InitializeAsync();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapPost("/__as/{user}", (string user, HttpContext ctx) => { ctx.Response.Cookies.Append("proof-user", user); return Results.Ok(); });
app.MapGet("/api/config", () => new { authMode = "insecureDev" });
app.MapGet("/api/sessions", () => Array.Empty<object>());
app.MapGet("/api/projects", () => Array.Empty<object>());
app.MapGet("/api/sessions/order", () => Array.Empty<string>());
app.MapGet("/api/admin/access", () => new { isAdmin = false });
app.Run("http://0.0.0.0:8080");

sealed class ProofAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Cookies["proof-user"] ?? "alice";
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
        foreach (var controller in feature.Controllers.ToArray())
            if (controller.AsType() != typeof(SharingController) && controller.AsType() != typeof(SharedSessionsController))
                feature.Controllers.Remove(controller);
    }
}
