using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Mcp;
using AgentHub.Api.Models;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using k8s;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// Persistence and access decisions are fixtures; HTTP downloads, parsing, paging, controllers
// and MCP tool methods are the production implementations. This does not prove cluster delivery.
var ct = CancellationToken.None;
var record = new SessionRecord { Id = "proof245", Owner = "owner", CallbackToken = "proof-callback-245", Status = "Running" };
var info = new SessionInfo { Id = record.Id, Owner = record.Owner, Title = "proof", Phase = "Running", PodIp = "127.0.0.1", Mode = SessionMode.Interactive };
Dictionary<string, string> captured = [];
var store = FixtureProxy.Make<ISessionStore>((name, _) => name switch {
    "GetByIdAsync" or "GetAsync" or "GetByCallbackTokenAsync" => Task.FromResult<SessionRecord?>(record),
    "GetTranscriptAsync" => Task.FromResult<string?>(captured["/transcript"]),
    "GetScrollbackAsync" => Task.FromResult<string?>(captured["/scrollback"]),
    "SetQuestionPendingAsync" => Task.CompletedTask,
    _ => throw new InvalidOperationException(name)
});
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AgentHub:AgentPort"] = args[0] }).Build();
var reader = new LiveTranscriptReader(new AgentPodRequests(http, new LoopbackIdentity()), store, config);
var live = await reader.ReadAsync(info, ct) ?? throw new Exception("No live entries");
Check(NativeTranscript.Render(live));
var page = await SessionTranscripts.PageAsync(null!, record.Owner, record.Id, "Running", null, null, ct, token => reader.ReadAsync(info, token));
Check(JsonSerializer.Serialize(page));
Console.WriteLine("PASS real LiveTranscriptReader over HTTP + live conversation paging");

for (var attempt = 0; attempt < 450; attempt++) {
    captured = JsonSerializer.Deserialize<Dictionary<string, string>>(await http.GetStringAsync(args[1] + "/captured"))!;
    if (captured.Count >= 4) break;
    await Task.Delay(100);
}
var native = await TranscriptReader.ReadTranscriptAsync(_ => Task.FromResult<string?>(captured["/objects/transcript"]), _ => Task.FromResult<string?>(captured["/transcript"]), ct);
var scroll = await TranscriptReader.ReadScrollbackAsync(_ => Task.FromResult<string?>(captured["/objects/scrollback"]), _ => Task.FromResult<string?>(captured["/scrollback"]), ct);
Check(native!); Check(scroll!);
var artifacts = FixtureProxy.Make<IArtifactStore>((name, values) => name == "GetTextAsync"
    ? Task.FromResult<string?>(captured[((string)values![0]!).EndsWith("transcript.jsonl") ? "/objects/transcript" : "/objects/scrollback"])
    : throw new InvalidOperationException(name));
using var cluster = new Kubernetes(new KubernetesClientConfiguration { Host = args[1] });
var backend = new KubernetesSessionService(config, store, null!, artifacts, null!, null!, null!, null!, null!, null!, null!, null!,
    NullLogger<KubernetesSessionService>.Instance, cluster: cluster);
var service = FixtureProxy.Make<ISessionService>((name, _) => name switch {
    "GetSessionAsync" => Task.FromResult<SessionInfo?>(info),
    "GetConversationAsync" => backend.GetConversationAsync(record.Owner, record.Id, ct),
    "GetTranscriptAsync" => backend.GetTranscriptAsync(record.Owner, record.Id, ct),
    _ => throw new InvalidOperationException(name)
});
var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "owner")], "proof")) };
var owner = new SessionsController(service, null!, [], liveTranscripts: reader) { ControllerContext = new ControllerContext { HttpContext = context } };
Check(JsonSerializer.Serialize(((OkObjectResult)(await owner.Conversation(record.Id, null, null, ct)).Result!).Value));
var remote = new RemoteController((_, _) => Task.FromResult<RemoteCaller?>(new("owner", null)), service) { ControllerContext = new ControllerContext { HttpContext = context } };
remote.Request.Headers.Authorization = "Bearer oah_proof-token";
Check(JsonSerializer.Serialize(((OkObjectResult)(await remote.Transcript(record.Id, null, null, ct)).Result!).Value));
var remoteRead = new RemoteReadController((_, _) => Task.FromResult<RemoteCaller?>(new("owner", null)), service, null!, null!, null!, null!, reader) { ControllerContext = new ControllerContext { HttpContext = context } };
Check(JsonSerializer.Serialize(((OkObjectResult)(await remoteRead.Conversation(record.Id, null, null, ct)).Result!).Value));
var access = FixtureProxy.Make<ISessionAccessService>((_, _) => Task.FromResult<SessionAccessResult?>(new(record, SessionAccessLevel.Viewer, "owner")));
var share = new SharedSessionsController(access);
Check(((ContentResult)await share.GetTranscript("proof-share", service, ct)).Content!);
Check(JsonSerializer.Serialize(((OkObjectResult)(await share.GetConversation("proof-share", null, null, service, ct)).Result!).Value));
var tools = new AgentHubMcpTools(new HttpContextAccessor { HttpContext = context }, service, NullLogger<AgentHubMcpTools>.Instance);
Check(await tools.GetSessionLogs(record.Id));
Check((await tools.GetSessionTranscript(record.Id)).Text);
Console.WriteLine("PASS real stored readers, owner/remote conversation, remote transcript, share transcript/conversation and MCP logs/transcript");
var notifier = new RecordingNotifier();
var relay = new InternalController(store, [notifier], backend, null!, [], [], null!, null!) {
    ControllerContext = new ControllerContext { HttpContext = context }
};
relay.Request.Headers["X-Agent-Token"] = record.CallbackToken;
await relay.Notify(record.Id, new InternalController.NotifyBody("proof-file-value-245 proof-env-value-245", "question"), ct);
Check(notifier.Message);
Console.WriteLine("PASS real callback controller + concrete session secret masking over Kubernetes SDK HTTP + notifier boundary");

static void Check(string text) {
    if (text.Contains("proof-file-value-245") || text.Contains("proof-env-value-245") ||
        !text.Contains("[secret:FILE_KEY]") || !text.Contains("[secret:ENV_KEY]")) throw new Exception("Secret masking regression: " + text);
}
public class FixtureProxy : DispatchProxy {
    public Func<string, object?[]?, object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args);
    public static T Make<T>(Func<string, object?[]?, object?> handler) where T : class {
        var proxy = Create<T, FixtureProxy>(); ((FixtureProxy)(object)proxy).Handler = handler; return proxy;
    }
}
sealed class LoopbackIdentity : IAgentPodIdentityResolver {
    public Task<bool> IsLiveSessionPodAsync(string id, IPAddress ip, CancellationToken ct = default) => Task.FromResult(id == "proof245" && IPAddress.IsLoopback(ip));
}
sealed class RecordingNotifier : INotifier {
    public string Message = "";
    public Task NotifyAsync(SessionRecord session, string kind, string message, CancellationToken ct = default) {
        Message = message; return Task.CompletedTask;
    }
}
