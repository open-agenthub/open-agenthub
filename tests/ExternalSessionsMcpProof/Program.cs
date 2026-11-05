using System.Reflection;
using System.Security.Claims;
using AgentHub.Api.Mcp;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

var builder = WebApplication.CreateBuilder(args);
var service = DispatchProxy.Create<ISessionService, EmptyService>();
builder.Services.AddControllers().AddApplicationPart(typeof(InternalController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyInternal()));
builder.Services.AddSingleton<ISessionService>(service);
builder.Services.AddSingleton<ApiTokenStore>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMcpServer().WithHttpTransport(ShareLinkMcpEndpoint.Configure)
    .WithTools<AgentHubMcpTools>().WithTools<ShareLinkMcpTools>();
builder.Services.AddSingleton<ISharedSessionHistory>(new SharedSessionHistory(service));
builder.Services.AddSingleton<ExternalShareLinkService>();
builder.Services.AddSingleton<PostgresSessionStore>();
builder.Services.AddSingleton<SessionShareStore>();
builder.Services.AddSingleton<ISessionAccessStore>(s => s.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<IEnterpriseLicense, ProofLicense>();
builder.Services.AddSingleton<ISessionAccessService, SessionAccessService>();
builder.Services.AddSingleton<ISessionMessageStore, PostgresSessionMessageStore>();
builder.Services.AddSingleton<ISessionSharingService>(s => new SessionSharingService(
    s.GetRequiredService<SessionShareStore>(), s.GetRequiredService<IEnterpriseLicense>(), null));
builder.Services.AddTransient<InternalController>(s => new InternalController(
    s.GetRequiredService<PostgresSessionStore>(), [], service,
    null!, [], [], null!, null!, messages: s.GetRequiredService<ISessionMessageStore>(),
    access: s.GetRequiredService<ISessionAccessService>(), sharing: s.GetRequiredService<ISessionSharingService>()));
builder.Services.AddTransient<RemoteController>(s => new RemoteController(
    s.GetRequiredService<ApiTokenStore>(), service, s.GetRequiredService<ISessionMessageStore>(),
    access: s.GetRequiredService<ISessionAccessService>(), sharing: s.GetRequiredService<ISessionSharingService>()));
builder.Services.AddControllers().AddControllersAsServices();
var app = builder.Build();
var tokens = app.Services.GetRequiredService<ApiTokenStore>();
await tokens.InitializeAsync();
await tokens.CreateAsync("alice", "proof", "oah_external_proof_token");
app.Use(async (ctx, next) => {
    if (ctx.Request.Path.StartsWithSegments("/mcp") && !ctx.Request.Path.StartsWithSegments("/mcp/shared"))
    {
        var token = ctx.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
        var caller = await tokens.FindCallerByTokenAsync(token);
        if (caller is null) { ctx.Response.StatusCode = 401; return; }
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", caller.Owner)], "proof"));
    }
    await next();
});
app.MapMcp("/mcp");
app.MapMcp("/mcp/shared");
var sessions = app.Services.GetRequiredService<PostgresSessionStore>();
await sessions.InitializeAsync();
var users = new UserDirectory(builder.Configuration);
await users.InitializeAsync();
await users.RecordLoginAsync("alice", "alice@example.com", "Alice");
await users.RecordLoginAsync("bob", "bob@example.com", "Bob");
((EmptyService)service).Store = sessions;
await new PostgresProjectStore(builder.Configuration).InitializeAsync();
await new AgentHub.Api.Ee.Identity.UserGroupStore(builder.Configuration).InitializeAsync();
var shares = app.Services.GetRequiredService<SessionShareStore>();
await shares.InitializeAsync();
await app.Services.GetRequiredService<ISessionMessageStore>().InitializeAsync();
await sessions.UpsertAsync(new SessionRecord { Id = "a", Owner = "alice", Title = "Agent A", CallbackToken = "proof-a", Status = "Paused", ProjectId = "fleet", ParentSessionId = "parent" });
foreach (var (id, project, parent) in new (string, string?, string?)[] { ("peer", "fleet", null), ("parent", null, null), ("child", null, "a"), ("sibling", "fleet", "parent"), ("outside", "other", null) })
    await sessions.UpsertAsync(new SessionRecord { Id = id, Owner = "alice", Title = id, CallbackToken = $"proof-{id}", Status = "Paused", ProjectId = project, ParentSessionId = parent });
await sessions.UpsertAsync(new SessionRecord { Id = "b", Owner = "bob", Title = "Agent B", CallbackToken = "proof-b-secret", Status = "Paused", McpConfigJson = "private" });
await sessions.UpsertAsync(new SessionRecord { Id = "b-child", Owner = "bob", Title = "Agent B child", ParentSessionId = "b", CallbackToken = "proof-b-child", Status = "Paused" });
app.MapControllers();
// The fixture changes real grants; all MCP requests still use production callback authentication.
app.MapPost("/__role/{role}", async (string role) => {
    if ((await shares.ListForOwnerAsync("bob", "b")).Users.Any(s => s.Recipient == "alice"))
        await shares.DeleteDirectAsync("bob", "b", "alice");
    if (role == "link") await shares.CreateLinkAsync("bob", "b", ShareRole.Collaborator, null);
    else if (role != "none") await shares.UpsertDirectAsync("bob", "b", "alice", Enum.Parse<ShareRole>(role));
    return Results.Ok();
});
app.MapPost("/__link/{role}", async (string role) =>
    Results.Ok(await shares.CreateLinkAsync("bob", "b", Enum.Parse<ShareRole>(role), null)));
app.MapPost("/__revoke/{id}", async (string id) => {
    await shares.DeleteLinkAsync("bob", "b", id); return Results.Ok();
});
app.MapPost("/__expire/{id}", async (string id) => {
    await using var connection = new Npgsql.NpgsqlConnection(builder.Configuration.GetConnectionString("Postgres"));
    await connection.OpenAsync();
    await using var command = new Npgsql.NpgsqlCommand("UPDATE session_share_links SET expires_at=now()-interval '1 minute' WHERE id=@id", connection);
    command.Parameters.AddWithValue("id", id);
    await command.ExecuteNonQueryAsync(); return Results.Ok();
});
app.Run("http://127.0.0.1:8410");

public class EmptyService : DispatchProxy
{
    public PostgresSessionStore Store { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        nameof(ISessionService.ListSessionsAsync) => List((string)args![0]!),
        nameof(ISessionService.TouchActivityAsync) => Task.CompletedTask,
        nameof(ISessionService.GetConversationAsync) => Task.FromResult<IReadOnlyList<TranscriptEntry>?>(null),
        nameof(ISessionService.GetTranscriptAsync) => Task.FromResult<string?>("proof history proof-b-secret"),
        nameof(ISessionService.GetSessionAsync) => Get((string)args![0]!, (string)args[1]!),
        _ => throw new InvalidOperationException($"Unexpected service call: {method?.Name}")
    };
    private async Task<IReadOnlyList<SessionInfo>> List(string owner)
        => (await Store.ListAsync(owner)).Select(Info).ToList();
    private async Task<SessionInfo?> Get(string owner, string id)
        => await Store.GetAsync(owner, id) is { } record ? Info(record) : null;
    private static SessionInfo Info(SessionRecord record) => new()
    {
        Id = record.Id, Owner = record.Owner, Title = record.Title, Mode = record.Mode,
        Phase = record.Status, ProjectId = record.ProjectId, ParentSessionId = record.ParentSessionId
    };
}
sealed class ProofLicense : IEnterpriseLicense
{
    public LicenseStatus Status => new() { Present = true, Valid = true, Plan = "proof", Seats = 2 };
    public bool Enabled => true;
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
}
sealed class OnlyInternal : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var type in feature.Controllers.Where(t => t.AsType() != typeof(InternalController) && t.AsType() != typeof(RemoteController) && t.AsType() != typeof(RemoteShareLinkController)).ToArray())
            feature.Controllers.Remove(type);
    }
}
