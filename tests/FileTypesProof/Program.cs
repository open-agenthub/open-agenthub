// Real HTTP proof host for #260: the REAL session file controllers (owner, internal agent
// callback, shared link), the REAL SessionFileService, Postgres registry/session/share stores and
// S3ArtifactStore against a rootless Garage. Only the auth handler and an unused session service
// are stand-ins.
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Files;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;

var b = WebApplication.CreateBuilder(args);
b.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
b.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()))
    .AddApplicationPart(typeof(SessionFilesController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new Only()));
b.Services.AddAuthentication("t").AddScheme<AuthenticationSchemeOptions, As>("t", _ => { });
b.Services.AddAuthorization();
if (Environment.GetEnvironmentVariable("SCRUB") == "1") b.Services.KeepStreamTicketsOutOfLogs();
var options = b.Configuration.GetSection("Files").Get<SessionFileOptions>() ?? new SessionFileOptions();
b.Services.AddSingleton(options);
b.Services.AddSingleton<ISessionFileRegistry, PostgresSessionFileRegistry>();
var podMode = Environment.GetEnvironmentVariable("MODE") == "pod";
if (podMode) b.Services.AddSingleton<IArtifactStore, NullArtifactStore>();
else b.Services.AddSingleton<IArtifactStore, S3ArtifactStore>();
b.Services.AddSingleton<PostgresSessionStore>();
b.Services.AddSingleton<ISessionStore>(sp => sp.GetRequiredService<PostgresSessionStore>());
b.Services.AddSingleton<IAgentCallbackAuthorizer, AgentCallbackAuthorizer>();
if (podMode) b.Services.AddHttpClient<IAgentFileClient, AgentFileClient>();
else b.Services.AddSingleton<IAgentFileClient>(DispatchProxy.Create<IAgentFileClient, Unused>());
b.Services.AddSingleton<IProjectFileAccess, ProjectFileAccess>();
b.Services.AddSingleton<ISessionShareStatus>(sp => sp.GetRequiredService<SessionShareStore>());
b.Services.AddSingleton<ISessionService>(DispatchProxy.Create<ISessionService, LiveSession>());
b.Services.AddSingleton<ISessionFileService, SessionFileService>();
b.Services.AddSingleton<SessionFileStreamTickets>();
b.Services.AddSingleton<SessionShareStore>();
b.Services.AddSingleton<UserDirectory>();
b.Services.AddSingleton<ISessionAccessStore>(sp => sp.GetRequiredService<SessionShareStore>());
b.Services.AddSingleton<ISessionAccessService, SessionAccessService>();
b.Services.AddSingleton<IEnterpriseLicense>(new Lic());
var app = b.Build();
await app.Services.GetRequiredService<PostgresSessionStore>().InitializeAsync();
await app.Services.GetRequiredService<ISessionFileRegistry>().InitializeAsync();
await app.Services.GetRequiredService<UserDirectory>().InitializeAsync();
await app.Services.GetRequiredService<SessionShareStore>().InitializeAsync();
var sessions = app.Services.GetRequiredService<PostgresSessionStore>();
await sessions.UpsertAsync(new SessionRecord { Id = "s1", Owner = "alice", Title = "video proof",
    ProjectId = "project-proof", CallbackToken = "proof-token" });
await sessions.UpsertAsync(new SessionRecord { Id = "s2", Owner = "alice", Title = "sibling",
    ProjectId = "project-proof", CallbackToken = "sibling-token" });
SessionInfo Info() => new() { Id = "s1", Owner = "alice", Title = "video proof", Mode = SessionMode.Interactive,
    Phase = "Running", Agent = AgentKind.Claude, CreatedAt = DateTime.UtcNow };

app.UseAuthentication(); app.UseAuthorization();
app.MapPost("/__share/{role}", async (string role, SessionShareStore shares) =>
    Results.Ok(new { token = (await shares.CreateLinkAsync("alice", "s1",
        Enum.Parse<ShareRole>(role, true), DateTime.UtcNow.AddHours(1))).Token })).AllowAnonymous();
app.MapPost("/__direct/{user}", async (string user, SessionShareStore shares, UserDirectory users) =>
{
    await users.RecordLoginAsync("alice", "alice@example.com", "Alice");
    await users.RecordLoginAsync(user, $"{user}@example.com", user);
    return Results.Ok(await shares.UpsertDirectAsync("alice", "s1", user, ShareRole.Viewer));
}).AllowAnonymous();
app.MapDelete("/__direct/{user}", async (string user, SessionShareStore shares) =>
    { await shares.DeleteDirectAsync("alice", "s1", user); return Results.Ok(); }).AllowAnonymous();
app.MapGet("/api/config", () => Results.Json(new { authority = "" })).AllowAnonymous();
app.MapGet("/api/sessions", () => Results.Json(new[] { Info() })).AllowAnonymous();
app.MapGet("/api/sessions/s1", () => Results.Json(Info())).AllowAnonymous();
app.MapGet("/api/projects", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/usage/summary", () => Results.Json(new { })).AllowAnonymous();
app.MapGet("/api/usage/sessions", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/admin/access", () => Results.Json(new { })).AllowAnonymous();
app.MapGet("/api/credentials", () => Results.Json(Array.Empty<object>())).AllowAnonymous();
app.MapGet("/api/agents/allowed", () => Results.Json(new { agents = Array.Empty<string>() })).AllowAnonymous();
app.MapControllers();
app.Run($"http://127.0.0.1:{Environment.GetEnvironmentVariable("PROOF_PORT") ?? "8080"}");

sealed class Only : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        var keep = new[] { typeof(SessionFilesController), typeof(InternalSessionFilesController), typeof(SharedSessionFilesController), typeof(SharedSessionsController) };
        foreach (var c in feature.Controllers.Where(c => !keep.Contains(c.AsType())).ToArray())
            feature.Controllers.Remove(c);
    }
}
// The pod-backed path asks for the live session to find the pod; it is this machine.
public class LiveSession : DispatchProxy
{
    protected override object? Invoke(MethodInfo? m, object?[]? a) => m?.Name == "GetSessionAsync"
        ? Task.FromResult<SessionInfo?>(new SessionInfo { Id = "s1", Owner = "alice", Title = "video proof",
            Mode = SessionMode.Interactive, Phase = "Running", PodIp = "127.0.0.1" })
        : throw new NotSupportedException(m?.Name);
}
public class Unused : DispatchProxy
{
    protected override object? Invoke(MethodInfo? m, object?[]? a) => throw new NotSupportedException(m?.Name);
}
sealed class As(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e)
    : AuthenticationHandler<AuthenticationSchemeOptions>(o, l, e)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("preferred_username", "alice") }, "t")), "t")));
}
sealed class Lic : IEnterpriseLicense
{
    public LicenseStatus Status => new() { Valid = true, Present = true, Plan = "test", Seats = 10 };
    public bool Enabled => true;
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
}
