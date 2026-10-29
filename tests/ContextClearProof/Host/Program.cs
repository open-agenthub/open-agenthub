// The backend's side of the Workspace conversation for the rig's pod: the real LiveTranscriptReader
// (pod requests, byte-offset follow, NativeTranscript parse) and SessionTranscripts paging, which
// is what GET /api/sessions/{id}/conversation runs for a running session. Session storage and pod
// identity are fixtures; the pod is the rig's common/server.js on loopback.
//   PROOF_AGENT_PORT  the pod's port      PROOF_HOST_PORT  where /conversation is served
using System.Net;
using System.Reflection;
using AgentHub.Api.Browser;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

var agentPort = Environment.GetEnvironmentVariable("PROOF_AGENT_PORT") ?? throw new InvalidOperationException("PROOF_AGENT_PORT");
var hostPort = Environment.GetEnvironmentVariable("PROOF_HOST_PORT") ?? throw new InvalidOperationException("PROOF_HOST_PORT");
var builder = WebApplication.CreateBuilder(args);
builder.Configuration["AgentHub:AgentPort"] = agentPort;
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
var pods = new AgentPodRequests(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), new Identity());
var reader = new LiveTranscriptReader(pods, new Store(), builder.Configuration);
var sessions = DispatchProxy.Create<ISessionService, Sessions>();
app.MapGet("/conversation", async (int? offset, int? max, HttpContext http) =>
    Results.Json(await SessionTranscripts.PageAsync(sessions, "owner", Sessions.Session.Id, "Running", offset, max,
        http.RequestAborted, ct => reader.ReadAsync(Sessions.Session, ct))));
app.Run("http://127.0.0.1:" + hostPort);

sealed class Identity : IAgentPodIdentityResolver
{
    public Task<bool> IsLiveSessionPodAsync(string id, IPAddress ip, CancellationToken ct = default)
        => Task.FromResult(id == Sessions.Session.Id && ip.Equals(IPAddress.Loopback));
}

public class Sessions : DispatchProxy
{
    public static readonly SessionInfo Session = new()
    {
        Id = "s336", Owner = "owner", Title = "Proof", Phase = "Running", PodIp = "127.0.0.1",
        Agent = AgentKind.Codex, Mode = SessionMode.Interactive, UiMode = SessionUiMode.Terminal
    };

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "GetSessionAsync" => Task.FromResult<SessionInfo?>(Session),
        "GetConversationAsync" => Task.FromResult<IReadOnlyList<TranscriptEntry>?>(null),
        "GetTranscriptAsync" => Task.FromResult<string?>(null),
        _ => throw new NotSupportedException(method?.Name)
    };
}

sealed class Store : ISessionStore
{
    public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default)
        => Task.FromResult<SessionRecord?>(new SessionRecord { Id = id, Owner = "owner", CallbackToken = "rig336-token" });
    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
    public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => GetByIdAsync(id, ct);
    public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) => GetByIdAsync("s336", ct);
    public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
    public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<IReadOnlyList<SessionRecord>> ListExpiredAsync(DateTime now, int limit, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
    public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
}
