using System.Net;
using System.Reflection;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Permissions;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration["AgentHub:AgentPort"] = "19251";
builder.Services.AddControllers();
var app = builder.Build();
var store = new Store();
var pods = new AgentPodRequests(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), new Identity());
var reader = new LiveTranscriptReader(pods, store, builder.Configuration);
var sessions = DispatchProxy.Create<ISessionService, Sessions>();
var clear = new SessionContextClear(pods, store, builder.Configuration);
app.MapPost("/internal/sessions/{id}/context-clear", async (string id, ContextClearRequest body, HttpContext http) => {
    var controller = new InternalController(store, [], sessions, null!, [], [], null!, null!, contextClear: clear);
    controller.ControllerContext = new ControllerContext { HttpContext = http };
    var result = await controller.ClearContext(id, body, http.RequestAborted);
    await result.Result!.ExecuteResultAsync(controller.ControllerContext);
});
app.MapGet("/conversation", async (int? offset, int? max, HttpContext http) => {
    var page = await SessionTranscripts.PageAsync(sessions, "owner", "s251", "Running", offset, max,
        http.RequestAborted, ct => reader.ReadAsync(Sessions.Session, ct));
    return Results.Json(page);
});
app.MapGet("/long", (int? offset, int? max) => Results.Json(ConversationPage.FromEntries("s251", "Running", Sessions.Long, offset, max)));
app.MapGet("/stored", async () => {
    using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:19251/agenthub/transcript");
    request.Headers.Add("X-Agent-Token", "proof251");
    using var response = await new HttpClient().SendAsync(request);
    return Results.Json(ConversationPage.FromEntries("s251", "Running", NativeTranscript.Parse(await response.Content.ReadAsStringAsync()), -1, null));
});
app.Run("http://127.0.0.1:19252");

sealed class Identity : IAgentPodIdentityResolver {
    public Task<bool> IsLiveSessionPodAsync(string id, IPAddress ip, CancellationToken ct = default)
        => Task.FromResult(id == "s251" && ip.Equals(IPAddress.Loopback));
}
public class Sessions : DispatchProxy {
    public static readonly SessionInfo Session = new() { Id = "s251", Owner = "owner", Title = "Proof", Phase = "Running", PodIp = "127.0.0.1", Agent = AgentKind.Claude, Mode = SessionMode.Interactive, UiMode = SessionUiMode.Terminal };
    public static readonly TranscriptEntry[] Long = Enumerable.Range(0, 20000).Select(i => new TranscriptEntry(i % 2 == 0 ? "user" : "assistant", $"History message {i:D5}: " + new string('x', 200), null)).ToArray();
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch {
        "GetSessionAsync" => Task.FromResult<SessionInfo?>(Session),
        "GetConversationAsync" => Task.FromResult<IReadOnlyList<TranscriptEntry>?>(null),
        "GetTranscriptAsync" => Task.FromResult<string?>(null),
        _ => throw new NotSupportedException(method?.Name)
    };
}
