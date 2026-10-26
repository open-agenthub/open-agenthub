using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentHub.Api.Controllers;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using k8s;

// Real controllers, session service, database and cluster API; only unrelated providers are empty.
var b = WebApplication.CreateBuilder(args);
b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
    ["ConnectionStrings:Postgres"] = "Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres",
    ["AgentHub:Namespace"] = "agenthub-sessions", ["Browser:Enabled"] = "false",
    ["AgentHub:AgentPort"] = "7699", ["AgentHub:SpawnMcpEnabled"] = "false", ["Network:Enabled"] = "false",
    ["AgentHub:ClaudeAgentImage"] = "registry.example.com/agent:proof"
});
b.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter())).AddApplicationPart(typeof(ProjectsController).Assembly)
    .ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new Only()));
b.Services.AddAuthentication("proof").AddScheme<AuthenticationSchemeOptions, Identity>("proof", _ => {});
b.Services.AddAuthorization();
b.Services.AddSingleton<IProjectStore, PostgresProjectStore>();
b.Services.AddSingleton<ISessionStore, PostgresSessionStore>();
b.Services.AddSingleton<AgentHub.Api.Permissions.PermissionStore>();
b.Services.AddSingleton<ISessionService>(sp => new KubernetesSessionService(b.Configuration,
    sp.GetRequiredService<ISessionStore>(), sp.GetRequiredService<IProjectStore>(),
    Empty.Of<AgentHub.Api.Storage.IArtifactStore>(), Empty.Of<AgentHub.Api.Browser.IBrowserService>(),
    Empty.Of<IGitAuthService>(), new AgentHub.Api.Usage.UsageLimitService(Empty.Of<IUsageStore>(), Empty.Of<AgentHub.Api.Usage.IPersonalUsageLimitSource>(), []),
    new AllAgents(), Empty.Of<AgentHub.Api.Library.ILibraryAccess>(), Empty.Of<AgentHub.Api.Library.IMcpServerStore>(),
    Empty.Of<AgentHub.Api.Library.IEphemeralApiMcpStore>(), Empty.Of<AgentHub.Api.Library.IMcpGatewayTokenService>(),
    sp.GetRequiredService<ILogger<KubernetesSessionService>>(),
    cluster: new Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(Environment.GetEnvironmentVariable("PROOF_KUBECONFIG")))));
var app = b.Build();
await app.Services.GetRequiredService<IProjectStore>().InitializeAsync();
await app.Services.GetRequiredService<ISessionStore>().InitializeAsync();
app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
app.MapGet("/api/config", () => new { authority = "", gitEnabled = false });
app.MapGet("/api/credentials", () => new {});
app.MapGet("/api/credentials/accounts", () => new {});
app.MapGet("/api/agents/allowed", () => new { agents = new[] { "Claude" } });
app.MapGet("/api/mcp-servers", () => Array.Empty<object>());
app.MapGet("/api/secrets", () => Array.Empty<object>());
app.Run("http://127.0.0.1:8080");
sealed class Only : IApplicationFeatureProvider<ControllerFeature> {
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) {
        foreach (var c in feature.Controllers.ToArray()) if (c.AsType() != typeof(ProjectsController) && c.AsType() != typeof(SessionsController)) feature.Controllers.Remove(c);
    }
}
sealed class Identity(IOptionsMonitor<AuthenticationSchemeOptions> o, ILoggerFactory l, UrlEncoder e) : AuthenticationHandler<AuthenticationSchemeOptions>(o,l,e) {
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "proof-owner")], "proof")), "proof")));
}
