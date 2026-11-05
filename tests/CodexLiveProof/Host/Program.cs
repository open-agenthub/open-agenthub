// #452 proof host: the REAL conversation path of SessionsController.Conversation —
// LiveTranscriptReader (pod route by byte offset) + SessionTranscripts.PageAsync +
// ConversationPage — against the rig's real session agent. The stored copy is what the rig's
// session agent uploaded (PUT callback/transcript, parsed with the real NativeTranscript).
using System.Reflection;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var agentPort = int.Parse(Environment.GetEnvironmentVariable("AGENT_PORT") ?? "7681");
var stored = Environment.GetEnvironmentVariable("STORED_FILE") ?? "/tmp/stored.jsonl";
builder.Configuration["AgentHub:AgentPort"] = agentPort.ToString();
var app = builder.Build();

var store = Stub<ISessionStore>.Create(new()
{
    ["GetByIdAsync"] = _ => Task.FromResult<SessionRecord?>(new SessionRecord { Id = "s1", Owner = "dev", CallbackToken = "rig-token" }),
});
var svc = Stub<ISessionService>.Create(new()
{
    ["GetConversationAsync"] = _ =>
    {
        var text = File.Exists(stored) ? File.ReadAllText(stored) : "";
        var entries = NativeTranscript.Parse(text);
        return Task.FromResult<IReadOnlyList<TranscriptEntry>?>(entries.Count == 0 ? null : entries);
    },
    ["GetTranscriptAsync"] = _ => Task.FromResult<string?>(""),
});
var pods = new DirectPods();
var reader = new LiveTranscriptReader(pods, store, app.Configuration);
var session = new SessionInfo { Id = "s1", Title = "Codex proof", Owner = "dev", Mode = SessionMode.Interactive, Phase = "Running", PodIp = "127.0.0.1" };

app.MapGet("/api/sessions/s1/conversation", async (int? offset, int? max, CancellationToken ct) =>
{
    var started = DateTime.UtcNow;
    var page = await SessionTranscripts.PageAsync(svc, "dev", "s1", session.Phase, offset, max, ct, token => reader.ReadAsync(session, token));
    Console.WriteLine($"conversation offset={offset} -> offset={page.Offset} next={page.NextOffset} length={page.Length} in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
    return Results.Json(page);
});
app.Run("http://127.0.0.1:8090");

sealed class DirectPods : IAgentPodRequests
{
    private static readonly HttpClient Http = new();
    public Task ConfirmAsync(string sessionId, string? podIp, CancellationToken ct) => Task.CompletedTask;
    public Task<HttpResponseMessage> SendAsync(string sessionId, HttpRequestMessage request, HttpCompletionOption completion, TimeSpan timeout, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return Http.SendAsync(request, completion, cts.Token);
    }
}

class Stub<T> : DispatchProxy where T : class
{
    public Dictionary<string, Func<object?[]?, object?>> Handlers = new();
    public static T Create(Dictionary<string, Func<object?[]?, object?>> handlers)
    {
        var proxy = Create<T, Stub<T>>();
        ((Stub<T>)(object)proxy).Handlers = handlers;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (Handlers.TryGetValue(method!.Name, out var h)) return h(args);
        var rt = method.ReturnType;
        if (rt == typeof(Task)) return Task.CompletedTask;
        if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(rt.GetGenericArguments()[0]).Invoke(null, [null]);
        return rt.IsValueType ? Activator.CreateInstance(rt) : null;
    }
}
