using System.Reflection;
using System.Security.Claims;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Persistence;
using AgentHub.Api.WebSockets;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:8080");
builder.Services.AddControllers().AddApplicationPart(typeof(BrowserClipboardController).Assembly);
builder.Services.AddAuthorization();
var session = new SessionRecord { Id = "popout", CallbackToken = "proof-callback", Owner = "owner", Title = "Pop-out proof" };
var runtime = new BrowserRuntimeClient(new HttpClient());
var browsers = new ProofBrowser(runtime);
builder.Services.AddSingleton<IBrowserService>(browsers);
builder.Services.AddSingleton<IBrowserRuntimeClient>(runtime);
builder.Services.AddSingleton(ProofProxy.Create<ISessionStore>((method, _) =>
    method.Name == "GetAsync" ? Task.FromResult<SessionRecord?>(session) : throw new NotSupportedException(method.Name)));
builder.Services.AddSingleton(ProofProxy.Create<ISessionAccessService>((method, _) =>
    Task.FromResult<SessionAccessResult?>(new(session, SessionAccessLevel.Owner, null))));
var app = builder.Build();
app.UseWebSockets();
app.Use(async (context, next) => {
    if (context.Request.Path != "/api/config") {
        var key = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
        if (context.WebSockets.IsWebSocketRequest) key = context.Request.Query["access_token"];
        if (key != "popout-proof-key") { context.Response.StatusCode = 401; return; }
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "owner")], "proof"));
    }
    await next();
});
app.UseAuthorization();
app.MapGet("/api/config", () => new { authMode = "apiKey" });
app.Map("/ws/sessions/popout/browser", context => BrowserProxy.HandleAsync(context, "popout", true, browsers, app.Services.GetRequiredService<ILoggerFactory>()));
app.MapControllers();
app.Run();

public class ProofProxy : DispatchProxy {
    public Func<MethodInfo, object?[]?, object?> Call = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args);
    public static T Create<T>(Func<MethodInfo, object?[]?, object?> call) where T : class {
        var proxy = Create<T, ProofProxy>(); ((ProofProxy)(object)proxy).Call = call; return proxy;
    }
}
// Provisioning is outside this UI proof: the actual runtime is started by entrypoint.sh.
public class ProofBrowser(IBrowserRuntimeClient runtime) : IBrowserService {
    static readonly BrowserConnection Connection = new(new(BrowserPhase.Running), "http://127.0.0.1:9223", "127.0.0.1");
    public Task<BrowserConnection?> GetConnectionAsync(string id, CancellationToken ct = default) => Task.FromResult<BrowserConnection?>(Connection);
    public Task<BrowserSummary> GetSummaryAsync(string id, CancellationToken ct = default) => Task.FromResult(Connection.Browser);
    public Task ResizeAsync(string id, BrowserViewport viewport, CancellationToken ct = default) => runtime.ResizeAsync(Connection.PodIp, viewport, ct);
    public Task<BrowserConnection> EnsureAsync(SessionRecord session, System.Net.IPAddress ip, CancellationToken ct = default) => Task.FromResult(Connection);
    public Task<IReadOnlyDictionary<string, BrowserSummary>> GetSummariesAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default) => throw new NotSupportedException();
    public Task StopAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<BrowserStateUrls?> MintStateUrlsAsync(string lease, string token, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DeleteStateAsync(SessionRecord session, CancellationToken ct = default) => throw new NotSupportedException();
}
