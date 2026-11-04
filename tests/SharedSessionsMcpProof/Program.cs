using System.Reflection;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers().AddApplicationPart(typeof(InternalController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new OnlyInternal()));
builder.Services.AddSingleton<PostgresSessionStore>();
builder.Services.AddSingleton<SessionShareStore>();
builder.Services.AddSingleton<ISessionAccessStore>(s => s.GetRequiredService<SessionShareStore>());
builder.Services.AddSingleton<IEnterpriseLicense, ProofLicense>();
builder.Services.AddSingleton<ISessionAccessService, SessionAccessService>();
builder.Services.AddSingleton<ISessionMessageStore, PostgresSessionMessageStore>();
builder.Services.AddSingleton<ISessionSharingService>(s => new SessionSharingService(
    s.GetRequiredService<SessionShareStore>(), s.GetRequiredService<IEnterpriseLicense>(), null));
builder.Services.AddTransient<InternalController>(s => new InternalController(
    s.GetRequiredService<PostgresSessionStore>(), [], DispatchProxy.Create<ISessionService, EmptyService>(),
    null!, [], [], null!, null!, messages: s.GetRequiredService<ISessionMessageStore>(),
    access: s.GetRequiredService<ISessionAccessService>(), sharing: s.GetRequiredService<ISessionSharingService>()));
builder.Services.AddControllers().AddControllersAsServices();
var app = builder.Build();
var sessions = app.Services.GetRequiredService<PostgresSessionStore>();
await sessions.InitializeAsync();
var users = new UserDirectory(builder.Configuration);
await users.InitializeAsync();
await users.RecordLoginAsync("alice", "alice@example.com", "Alice");
await users.RecordLoginAsync("bob", "bob@example.com", "Bob");
var shares = app.Services.GetRequiredService<SessionShareStore>();
await shares.InitializeAsync();
await app.Services.GetRequiredService<ISessionMessageStore>().InitializeAsync();
await sessions.UpsertAsync(new SessionRecord { Id = "a", Owner = "alice", Title = "Agent A", CallbackToken = "proof-a", Status = "Paused" });
await sessions.UpsertAsync(new SessionRecord { Id = "b", Owner = "bob", Title = "Agent B", CallbackToken = "proof-b", Status = "Paused", McpConfigJson = "private" });
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
app.Run("http://127.0.0.1:8408");

public class EmptyService : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method?.Name == nameof(ISessionService.ListSessionsAsync)
            ? Task.FromResult<IReadOnlyList<SessionInfo>>([])
            : throw new InvalidOperationException($"Unexpected service call: {method?.Name}");
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
        foreach (var type in feature.Controllers.Where(t => t.AsType() != typeof(InternalController)).ToArray())
            feature.Controllers.Remove(type);
    }
}
