// Runs the real SlackSocketModeService (as the hosted service it is in the backend) against a
// local stand-in for Slack: apps.connections.open hands out a ws:// URL served here, the
// envelopes arrive over that socket exactly as Socket Mode delivers them, and every Web API
// write (chat.postMessage, chat.postEphemeral, chat.update, chat.delete) is recorded. A stand-in
// session pod records what is typed into the terminal. Stores are the real Postgres ones.
//
// Usage: AGENTHUB_TEST_POSTGRES_CONNECTION_STRING="Host=...;Username=...;Database=..." dotnet run
// Exit code 0 when every expectation holds; the transcript goes to stdout.
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Browser;
using AgentHub.Api.Chat;
using AgentHub.Api.Ee.Identity;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Ee.Slack;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

var baseCs = Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING")
             ?? throw new InvalidOperationException("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING is required");
var schema = $"slack_sender_proof_{Guid.NewGuid():N}";
await using (var c = new NpgsqlConnection(baseCs))
{
    await c.OpenAsync();
    await new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", c).ExecuteNonQueryAsync();
}
var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConnectionStrings:Postgres"] = new NpgsqlConnectionStringBuilder(baseCs) { SearchPath = schema }.ConnectionString
}).Build();

var failures = new List<string>();
void Expect(bool ok, string what) { Console.WriteLine($"  [{(ok ? "ok" : "FAIL")}] {what}"); if (!ok) failures.Add(what); }

try
{
    // --- Real stores -------------------------------------------------------------------------
    var sessionsStore = new PostgresSessionStore(cfg); await sessionsStore.InitializeAsync();
    var users = new UserDirectory(cfg); await users.InitializeAsync();
    await new PostgresProjectStore(cfg).InitializeAsync();
    await new UserGroupStore(cfg).InitializeAsync();
    var shares = new SessionShareStore(cfg, NullLogger<SessionShareStore>.Instance); await shares.InitializeAsync();
    await new ChatBindingStore(cfg).InitializeAsync();
    var threads = new SlackThreadStore(cfg); await threads.InitializeAsync();
    var permissions = new PermissionStore(cfg); await permissions.InitializeAsync();

    foreach (var owner in new[] { "alice", "bob", "vic", "mallory" })
        await users.RecordLoginAsync(owner, $"{owner}@example.com", owner);
    foreach (var id in new[] { "s-channel", "s-dm" })
        await sessionsStore.UpsertAsync(new SessionRecord
        {
            Id = id, Owner = "alice", Title = id, Mode = SessionMode.Interactive, CallbackToken = $"cb-{id}"
        });
    await shares.UpsertDirectAsync("alice", "s-channel", "bob", ShareRole.Collaborator);
    await shares.UpsertDirectAsync("alice", "s-channel", "vic", ShareRole.Viewer);
    await threads.UpsertAsync(new SlackThread("s-channel", "alice", "C0PROOF", "1700000000.000100"));
    await threads.UpsertAsync(new SlackThread("s-dm", "alice", "D0ALICE", "1700000000.000200"));
    foreach (var req in new[] { "req-1", "req-2" })
    {
        await permissions.CreateAsync(new PermissionRequest { Id = req, SessionId = "s-channel", Owner = "alice", Tool = $"Bash ({req})" });
        await permissions.SetPromptMessageAsync(req, "slack", "C0PROOF", $"1700000000.{req[^1]}00300");
    }

    // --- Stand-in session pod: records terminal input frames ---------------------------------
    var typed = new ConcurrentQueue<string>();
    var podBuilder = WebApplication.CreateBuilder();
    podBuilder.Logging.ClearProviders();
    podBuilder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var pod = podBuilder.Build();
    pod.UseWebSockets();
    pod.Map("/", async ctx =>
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync("tty");
        var buf = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            var r = await ws.ReceiveAsync(buf, ctx.RequestAborted);
            if (r.MessageType == WebSocketMessageType.Close) break;
            using var frame = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
            var data = frame.RootElement.GetProperty("data").GetString()!;
            if (data != "\r") typed.Enqueue(data);
        }
    });
    await pod.StartAsync();
    var agentPort = new Uri(pod.Urls.Single()).Port;

    // --- Stand-in Slack: Web API + Socket Mode -----------------------------------------------
    var emails = new Dictionary<string, string?>
    {
        ["U_ALICE"] = "alice@example.com", ["U_BOB"] = "bob@example.com", ["U_VIC"] = "vic@example.com",
        ["U_MALLORY"] = "mallory@example.com", ["U_STRANGER"] = "stranger@example.com"
    };
    var calls = new ConcurrentQueue<(string Method, JsonElement Body)>();
    var acks = new ConcurrentQueue<string>();
    var socketReady = new TaskCompletionSource<WebSocket>(TaskCreationOptions.RunContinuationsAsynchronously);
    var socketDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var slackBuilder = WebApplication.CreateBuilder();
    slackBuilder.Logging.ClearProviders();
    slackBuilder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var slack = slackBuilder.Build();
    slack.UseWebSockets();
    string slackOrigin = "";
    slack.MapPost("/api/apps.connections.open", () =>
        Results.Json(new { ok = true, url = slackOrigin.Replace("http://", "ws://") + "/socket" }));
    slack.MapGet("/api/users.info", (string user) => emails.TryGetValue(user, out var email)
        ? Results.Json(new { ok = true, user = new { id = user, profile = new { email } } })
        : Results.Json(new { ok = false, error = "user_not_found" }));
    var ts = 0;
    slack.MapPost("/api/{method}", async (string method, HttpRequest request) =>
    {
        using var body = await JsonDocument.ParseAsync(request.Body);
        calls.Enqueue((method, body.RootElement.Clone()));
        return Results.Json(new { ok = true, ts = $"1700000099.{Interlocked.Increment(ref ts):D6}" });
    });
    slack.Map("/socket", async ctx =>
    {
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        socketReady.TrySetResult(ws);
        var buf = new byte[16 * 1024];
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult r;
            try { r = await ws.ReceiveAsync(buf, ctx.RequestAborted); } catch { break; }
            if (r.MessageType == WebSocketMessageType.Close) break;
            using var ack = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
            acks.Enqueue(ack.RootElement.GetProperty("envelope_id").GetString()!);
        }
        await socketDone.Task;
    });
    await slack.StartAsync();
    slackOrigin = slack.Urls.Single();

    // --- The real service, wired as Program.cs wires it -----------------------------------
    var opts = new SlackOptions { Enabled = true, BotToken = "xoxb-proof", AppToken = "xapp-proof" };
    using var http = new LoopbackFactory(new Uri(slackOrigin));
    var client = new SlackClient(http, opts, NullLogger<SlackClient>.Instance);
    var license = new Licensed();
    var gate = new SlackSenderGate(client, users, new SessionAccessService(shares, license));
    var sessionService = DispatchProxy.Create<ISessionService, RunningSessions>();
    ((RunningSessions)sessionService).Store = sessionsStore;
    var pods = new AgentPodRequests(new HttpClient(), new AlwaysLive());
    var serviceCfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["AgentHub:AgentPort"] = agentPort.ToString()
    }).Build();
    using var service = new SlackSocketModeService(opts, license, client, threads, permissions, sessionService, users,
        new ChatRepoService(null!, NullLogger<ChatRepoService>.Instance), new WorkingIndicator(), pods, gate,
        serviceCfg, NullLogger<SlackSocketModeService>.Instance);
    await service.StartAsync(CancellationToken.None);
    var socket = await socketReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
    Console.WriteLine($"Socket Mode connected (stand-in Slack {slackOrigin}, stand-in pod port {agentPort}).");

    var n = 0;
    async Task Deliver(string label, object envelope)
    {
        var id = $"env-{++n}";
        var json = JsonSerializer.Serialize(envelope).Replace("\"__ENV__\"", $"\"{id}\"");
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
        // The service handles envelopes one after another and acks each before handling it,
        // so the ack of a following no-op envelope means this one is fully handled.
        var fence = $"fence-{n}";
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "noop", envelope_id = fence })),
            WebSocketMessageType.Text, true, CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!acks.Contains(fence) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Console.WriteLine($"> {label}  (acked: {acks.Contains(id)})");
    }
    object Text(string user, string text, string channel = "C0PROOF", string thread = "1700000000.000100") => new
    {
        type = "events_api", envelope_id = "__ENV__",
        payload = new { @event = new { type = "message", user, text, channel, thread_ts = thread, ts = "1700000001.000001" } }
    };
    object Click(string user, string decision, string req) => new
    {
        type = "interactive", envelope_id = "__ENV__",
        payload = new
        {
            type = "block_actions", user = new { id = user, username = user.ToLowerInvariant() },
            container = new { type = "message", channel_id = "C0PROOF", message_ts = $"1700000000.{req[^1]}00300", thread_ts = "1700000000.000100" },
            actions = new[] { new { action_id = PermissionAction.Id(decision, req), value = req } }
        }
    };

    List<(string Method, JsonElement Body)> Since(int mark) => calls.Skip(mark).ToList();
    static string Show((string Method, JsonElement Body) c)
    {
        var b = c.Body;
        string? P(string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return $"{c.Method} channel={P("channel")} user={P("user") ?? "-"} thread={P("thread_ts") ?? "-"} text={JsonSerializer.Serialize(P("text"))}";
    }
    void Captured(int mark) { foreach (var c in Since(mark)) Console.WriteLine($"    slack: {Show(c)}"); }

    Console.WriteLine("\n== Thread replies in a channel thread ==");
    foreach (var (user, text) in new[]
             {
                 ("U_MALLORY", "mallory-secret-1"), ("U_MALLORY", "mallory-secret-2"), ("U_MALLORY", "!status"),
                 ("U_VIC", "viewer-secret"), ("U_STRANGER", "stranger-secret-1"), ("U_STRANGER", "stranger-secret-2"),
                 ("U_BOB", "collaborator-reply"), ("U_ALICE", "owner-reply"), ("U_ALICE", "!status")
             })
    {
        var mark = calls.Count; var typedBefore = typed.Count;
        await Deliver($"{user}: {text}", Text(user, text));
        foreach (var t in typed.Skip(typedBefore)) Console.WriteLine($"    pod typed: {JsonSerializer.Serialize(t)}");
        Captured(mark);
    }

    Console.WriteLine("\n== Owner in the DM thread ==");
    {
        var mark = calls.Count; var typedBefore = typed.Count;
        await Deliver("U_ALICE (DM): owner-dm-reply", Text("U_ALICE", "owner-dm-reply", "D0ALICE", "1700000000.000200"));
        foreach (var t in typed.Skip(typedBefore)) Console.WriteLine($"    pod typed: {JsonSerializer.Serialize(t)}");
        Captured(mark);
    }

    Console.WriteLine("\n== Permission buttons ==");
    var decisionsAfterRefused = new List<string?>();
    foreach (var (user, decision) in new[]
             {
                 ("U_MALLORY", "allow"), ("U_MALLORY", "allowAlways"), ("U_MALLORY", "deny"),
                 ("U_VIC", "allow"), ("U_STRANGER", "deny")
             })
    {
        var mark = calls.Count;
        await Deliver($"{user} clicks {decision} on req-1", Click(user, decision, "req-1"));
        decisionsAfterRefused.Add(await permissions.GetDecisionAsync("req-1"));
        Console.WriteLine($"    req-1 decision: {decisionsAfterRefused[^1] ?? "(pending)"}");
        Captured(mark);
    }
    var markOwner = calls.Count;
    await Deliver("U_ALICE clicks allow on req-1", Click("U_ALICE", "allow", "req-1"));
    var req1 = await permissions.GetDecisionAsync("req-1");
    Console.WriteLine($"    req-1 decision: {req1}");
    Captured(markOwner);
    var markLate = calls.Count;
    await Deliver("U_MALLORY clicks deny on decided req-1", Click("U_MALLORY", "deny", "req-1"));
    Captured(markLate);
    var markBob = calls.Count;
    await Deliver("U_BOB clicks deny on req-2", Click("U_BOB", "deny", "req-2"));
    var req2 = await permissions.GetDecisionAsync("req-2");
    Console.WriteLine($"    req-2 decision: {req2}");
    Captured(markBob);

    socketDone.TrySetResult();
    await service.StopAsync(CancellationToken.None);

    Console.WriteLine("\n== Expectations ==");
    var all = calls.ToList();
    var typedAll = typed.ToList();
    Expect(typedAll.SequenceEqual(["collaborator-reply", "owner-reply", "owner-dm-reply"]),
        $"pod received exactly the collaborator's and owner's replies ({string.Join(", ", typedAll)})");
    Expect(!all.Any(c => c.Body.GetRawText().Contains("secret")), "no refused text appears in any Slack call");
    var ephemerals = all.Where(c => c.Method == "chat.postEphemeral").ToList();
    string? Prop(JsonElement b, string k) => b.TryGetProperty(k, out var v) ? v.GetString() : null;
    Expect(ephemerals.Count(e => Prop(e.Body, "user") == "U_MALLORY") == 1, "mallory is told once (text, !status and three clicks)");
    Expect(ephemerals.Count(e => Prop(e.Body, "user") == "U_VIC") == 1, "viewer is told once");
    Expect(ephemerals.Count(e => Prop(e.Body, "user") == "U_STRANGER" && Prop(e.Body, "text") == SlackSenderGate.UnlinkedNotice) == 1,
        "unlinked user is told once how linking works");
    Expect(ephemerals.All(e => Prop(e.Body, "user") is "U_MALLORY" or "U_VIC" or "U_STRANGER"), "owner and collaborator get no refusal");
    Expect(decisionsAfterRefused.All(d => d is null), "refused clicks leave req-1 pending");
    Expect(req1 == "allow" && req2 == "deny", "owner's allow and collaborator's deny are recorded");
    Expect(!await permissions.IsAlwaysAllowedAsync("s-channel", "Bash (req-1)"), "refused allowAlways added no rule");
    Expect(all.Count(c => c.Method == "chat.update" && Prop(c.Body, "text")!.Contains("Bash (req-1)")) == 1,
        "req-1's prompt is rewritten once (by the owner's click), not by the late refused click");
    Expect(all.Count(c => c.Method == "chat.postMessage" && (Prop(c.Body, "text") ?? "").Contains("Status")) <= 1,
        "only the owner's !status is answered");
}
finally
{
    await using var c = new NpgsqlConnection(baseCs);
    await c.OpenAsync();
    await new NpgsqlCommand($"DROP SCHEMA \"{schema}\" CASCADE", c).ExecuteNonQueryAsync();
}

Console.WriteLine(failures.Count == 0 ? "\nPROOF PASSED" : $"\nPROOF FAILED: {failures.Count}");
return failures.Count == 0 ? 0 : 1;

sealed class LoopbackFactory(Uri origin) : IHttpClientFactory, IDisposable
{
    private readonly Rewrite _handler = new(origin);
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    public void Dispose() => _handler.Dispose();

    private sealed class Rewrite(Uri origin) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host != "slack.com") throw new InvalidOperationException($"unexpected host {request.RequestUri}");
            request.RequestUri = new Uri(origin, request.RequestUri.PathAndQuery);
            return base.SendAsync(request, ct);
        }
    }
}

sealed class Licensed : IEnterpriseLicense
{
    public LicenseStatus Status => new() { Valid = true, Present = true };
    public bool Enabled => true;
    public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
}

sealed class AlwaysLive : IAgentPodIdentityResolver
{
    public Task<bool> IsLiveSessionPodAsync(string sessionId, IPAddress sourceIp, CancellationToken ct = default)
        => Task.FromResult(true);
}

public class RunningSessions : DispatchProxy
{
    public PostgresSessionStore? Store { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        nameof(ISessionService.GetSessionAsync) => Get((string)args![0]!, (string)args[1]!),
        nameof(ISessionService.TouchActivityAsync) => Task.CompletedTask,
        _ => throw new InvalidOperationException($"Unexpected session operation: {method?.Name}")
    };

    private async Task<SessionInfo?> Get(string owner, string id)
        => await Store!.GetAsync(owner, id) is { } r
            ? new SessionInfo { Id = r.Id, Owner = r.Owner, Title = r.Title, Mode = r.Mode, Phase = "Running", PodIp = "127.0.0.1" }
            : null;
}
