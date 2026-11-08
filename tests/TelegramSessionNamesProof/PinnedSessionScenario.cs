using System.Net;
using AgentHub.TestSupport;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Browser;
using AgentHub.Api.Chat;
using AgentHub.Api.Chat.Telegram;
using AgentHub.Api.Library;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace TelegramSessionNamesProof;

/// <summary>
/// Session lookups for the router: a session exists while its row does, and runs at the pod
/// address given for it. The real delete removes the row, so the router sees what it would see
/// in production after a delete.
/// </summary>
public class RunningSessions : DispatchProxy
{
    public PostgresSessionStore Store = null!;
    public Dictionary<string, string> PodIps = new();

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        nameof(ISessionService.GetSessionAsync) => GetAsync((string)args![0]!, (string)args[1]!),
        nameof(ISessionService.ListSessionsAsync) => ListAsync((string)args![0]!),
        nameof(ISessionService.TouchActivityAsync) => Task.CompletedTask,
        _ => throw new NotSupportedException(method.Name)
    };

    private async Task<SessionInfo?> GetAsync(string owner, string id)
        => await Store.GetAsync(owner, id) is { } r ? Info(r) : null;

    private async Task<IReadOnlyList<SessionInfo>> ListAsync(string owner)
        => (await Store.ListAsync(owner)).Select(Info).ToList();

    private SessionInfo Info(SessionRecord r) => new()
    {
        Id = r.Id, Owner = r.Owner, Title = r.Title, Mode = r.Mode, Phase = "Running",
        PodIp = PodIps.GetValueOrDefault(r.Id), LastActivityAt = r.LastActivityAt, Agent = AgentKind.Claude
    };
}

/// <summary>One step of the pinned-session run: what went over the Bot API, what each pod's terminal received.</summary>
public record PinnedStep(string Step, List<string> Replies, Dictionary<string, List<string>> Typed, string? Active, string? Pin);

/// <summary>
/// #398 end to end: /use A, then B posts, then a plain message. Production router, notifier,
/// conversation factory and binding store on Postgres, the production Bot API client against a
/// loopback recorder, the real <see cref="AgentTerminal"/> WebSocket into one stand-in pod per
/// session (127.0.0.2 = A, 127.0.0.3 = B, 127.0.0.4 = C), and the real
/// <see cref="KubernetesSessionService"/> delete against a Kubernetes API stand-in. Runs in a
/// private chat and in a forum supergroup, where each session gets its own topic.
/// </summary>
public static class PinnedSessionScenario
{
    private const string Owner = "owner";
    private const string Chat = "5";
    private const string A = "aaaa0398", B = "bbbb0398", C = "cccc0398";

    public static async Task<List<PinnedStep>> RunAsync(string connectionString, bool forum, bool verify = true)
    {
        using var bot = LoopbackHttpListener.Start(out var port);
        var pods = new Dictionary<string, string> { [A] = "127.0.0.2", [B] = "127.0.0.3", [C] = "127.0.0.4" };
        var typed = pods.Keys.ToDictionary(id => id, _ => new List<string>());
        // One listener for all pods: the address a connection arrived on says whose terminal it is.
        using var podListener = LoopbackHttpListener.Start(p => pods.Values.Select(ip => $"http://{ip}:{p}/"), out var agentPort);
        var byIp = pods.ToDictionary(p => p.Value, p => typed[p.Key]);
        var wire = new List<string>();
        using var stop = new CancellationTokenSource();
        var servers = new List<Task> { Task.Run(() => ServeBotAsync(bot, wire, stop.Token)) };
        servers.Add(Task.Run(() => ServePodsAsync(podListener, byIp, stop.Token)));

        var schema = "tgpin_" + Guid.NewGuid().ToString("N");
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"CREATE SCHEMA {schema}", db)) await cmd.ExecuteNonQueryAsync();
        try
        {
            // Unpooled for the same reason as the deleted-session scenario: stores nobody disposes.
            var cs = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Pooling = false }.ToString();
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = cs, ["FrontendOrigin"] = "https://example.com",
                ["AgentHub:Namespace"] = "proof", ["AgentHub:SkillLibraryMcp"] = "false",
                ["AgentHub:AgentPort"] = agentPort.ToString()
            }).Build();
            var store = new PostgresSessionStore(cfg); await store.InitializeAsync();
            var users = new UserDirectory(cfg); await users.InitializeAsync();
            await users.RecordLoginAsync(Owner, null, null);
            await users.SetTelegramLinkAsync(Owner, Chat, forum);
            var bindings = new ChatBindingStore(cfg); await bindings.InitializeAsync();
            var permissions = new PermissionStore(cfg); await permissions.InitializeAsync();
            var codes = new ChatLinkCodeStore(cfg); await codes.InitializeAsync();
            await using var raw = NpgsqlDataSource.Create(cs);

            var records = new Dictionary<string, SessionRecord>();
            foreach (var (id, title) in new[] { (A, "Release notes"), (B, "Nightly check"), (C, "Dependency bump") })
            {
                records[id] = new SessionRecord
                {
                    Id = id, Owner = Owner, Title = title, Mode = SessionMode.Interactive, CallbackToken = "cb-" + id,
                    CreatedAt = DateTime.UtcNow.AddHours(-1), LastActivityAt = DateTime.UtcNow
                };
                await store.UpsertAsync(records[id]);
            }

            var lookup = DispatchProxy.Create<ISessionService, RunningSessions>();
            ((RunningSessions)lookup).Store = store;
            ((RunningSessions)lookup).PodIps = pods;
            using var cluster = new k8s.Kubernetes(new k8s.KubernetesClientConfiguration { Host = $"http://127.0.0.1:{port}/" });
            var sessions = new KubernetesSessionService(cfg, store, DispatchProxy.Create<IProjectStore, Unused>(), new NullArtifactStore(),
                DispatchProxy.Create<IBrowserService, NoBrowsers>(), DispatchProxy.Create<IGitAuthService, Unused>(), null!,
                new AgentHub.Api.Agents.AllowAllAgentsProvider(), DispatchProxy.Create<ILibraryAccess, Unused>(),
                DispatchProxy.Create<IMcpServerStore, Unused>(), new InMemoryEphemeralApiMcpStore(), null!,
                NullLogger<KubernetesSessionService>.Instance, runtimeUpdateLock: new PostgresSessionExpiryLock(cfg),
                cluster: cluster, chatCleanup: bindings);

            var opts = new TelegramOptions { Enabled = true, BotToken = "stand-in" };
            using var factory = new LocalBotFactory(new Uri($"http://127.0.0.1:{port}/"));
            var tg = new TelegramClient(factory, opts, NullLogger<TelegramClient>.Instance);
            var conversations = new TelegramConversationFactory(tg, bindings, users, cfg, NullLogger<TelegramConversationFactory>.Instance);
            var indicator = new WorkingIndicator(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));
            var notifier = new TelegramNotifier(opts, tg, bindings, users, conversations, indicator, NullLogger<TelegramNotifier>.Instance);
            var update = new TelegramUpdateService(opts, tg, bindings, codes, users, permissions, lookup, conversations,
                new ChatRepoService(null!, NullLogger<ChatRepoService>.Instance), indicator,
                new AgentPodRequests(new HttpClient(), new LocalPod()), cfg, NullLogger<TelegramUpdateService>.Instance);

            var steps = new List<PinnedStep>();
            async Task<string?> Pin()
            {
                // -1-style guard: the baseline revision has no pin table.
                await using var exists = raw.CreateCommand("SELECT to_regclass('chat_pins') IS NOT NULL");
                if (await exists.ExecuteScalarAsync() is not true) return "(no pin table)";
                await using var q = raw.CreateCommand("SELECT session_id FROM chat_pins WHERE chat_id = @c");
                q.Parameters.AddWithValue("c", Chat);
                return (string?)await q.ExecuteScalarAsync();
            }
            async Task Step(string name, Func<Task> action)
            {
                int before;
                lock (wire) before = wire.Count;
                var typedBefore = typed.ToDictionary(t => t.Key, t => { lock (t.Value) return t.Value.Count; });
                await action();
                indicator.Stop(A); indicator.Stop(B); indicator.Stop(C);
                List<string> replies;
                lock (wire) replies = wire.Skip(before).Where(w => w.StartsWith("sendMessage", StringComparison.Ordinal)).ToList();
                var newTyped = typed.ToDictionary(t => t.Key, t => { lock (t.Value) return t.Value.Skip(typedBefore[t.Key]).ToList(); })
                    .Where(t => t.Value.Count > 0).ToDictionary(t => t.Key, t => t.Value);
                steps.Add(new(name, replies, newTyped, (await bindings.GetActiveAsync("telegram", Chat))?.SessionId, await Pin()));
            }
            Task Post(string id, string text) => notifier.NotifyAsync(records[id], "question", text);
            Task Send(string text, string? thread = null) => DispatchAsync(update, text, forum, thread);
            async Task<string?> Topic(string id) => (await bindings.GetAsync("telegram", id))?.ThreadId;

            await Step("1 A asks a question (first contact)", () => Post(A, "Which changelog format?"));
            await Step("2 B asks a question, no /use yet", () => Post(B, "Run the nightly now?"));
            await Step("3 /use A", () => Send("/use aaaa"));
            await Step("4 B asks again", () => Post(B, "Still waiting: run the nightly now?"));
            await Step("5 C asks its first question", () => Post(C, "Bump to the new major?"));
            await Step("6 plain message", () => Send("Keep a changelog, please"));
            await Step("7 message in B's own topic (forum) or a reply-less plain message (private)",
                async () => { if (forum) await Send("Yes, run it", await Topic(B)); });
            await Step("8 delete A through KubernetesSessionService.DeleteSessionAsync", () => sessions.DeleteSessionAsync(Owner, A));
            await Step("9 B asks again after the delete", () => Post(B, "Anyone?"));
            await Step("10 plain message after the delete", () => Send("Are you there?"));
            await Step("11 /status after the delete", () => Send("/status"));
            await Step("12 /unpin", () => Send("/unpin"));
            await Step("13 B asks again", () => Post(B, "Run the nightly now?"));
            await Step("14 plain message", () => Send("Go ahead"));
            await Step("15 /use auto when nothing is pinned", () => Send("/use auto"));

            if (verify) Verify(steps, forum);
            return steps;
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(servers);
            await using var cmd = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", db);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public static void Verify(List<PinnedStep> s, bool forum)
    {
        Check(s[0].Active == A && s[1].Active == B, "without a pin the newest session to speak becomes active");
        Check(s[2].Active == A && s[2].Pin == A && Has(s[2], "Plain replies now go to Release notes (#aaaa) until you choose another"), "/use pins A");
        Check(s[3].Active == A && s[4].Active == A, "B and C posting leave the pinned A active");
        Check(Has(s[3], "Nightly check (#bbbb)") && Has(s[4], "Dependency bump (#cccc)"), "their messages are still posted with their names");
        Check(Typed(s[5], A) == "Keep a changelog, please" && !s[5].Typed.ContainsKey(B), "a plain message reaches A, not B");
        if (forum)
            Check(Typed(s[6], B) == "Yes, run it" && !s[6].Typed.ContainsKey(A) && s[6].Active == A,
                "a message in B's topic goes to B and leaves the pin alone");
        Check(s[7].Pin == A && s[7].Active is null, "after the delete the pin remains and nothing is active");
        Check(s[8].Active is null, "B posting after the delete does not take over");
        Check(s[9].Typed.Count == 0 && Has(s[9], "🗑 The session you selected with /use, Release notes (#aaaa), was deleted."),
            "plain message after the delete gets the error and goes nowhere");
        Check(Has(s[10], "The session you selected with /use, Release notes (#aaaa), was deleted."), "/status says the same");
        Check(s[11].Pin is null && Has(s[11], "Plain replies follow the newest session again"), "/unpin removes the pin");
        Check(s[12].Active == B, "after /unpin the newest session becomes active again");
        Check(Typed(s[13], B) == "Go ahead", "and plain messages follow it");
        Check(Has(s[14], "Plain replies already follow the newest session."), "/use auto without a pin");
    }

    private static string? Typed(PinnedStep step, string id)
        => step.Typed.TryGetValue(id, out var frames) ? string.Concat(frames.Where(f => f != "\r")) : null;

    private static bool Has(PinnedStep step, string text) => step.Replies.Any(r => r.Contains(text, StringComparison.Ordinal));

    private static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("Pinned-session proof failed: " + what);
    }

    private static async Task DispatchAsync(TelegramUpdateService update, string text, bool forum, string? thread)
    {
        var message = new Dictionary<string, object?>
        {
            ["message_id"] = 1,
            ["chat"] = forum ? new { id = 5, type = "supergroup", is_forum = true } : (object)new { id = 5, type = "private", is_forum = false },
            ["from"] = new { id = 1, is_bot = false }, ["text"] = text
        };
        if (thread is not null) message["message_thread_id"] = int.Parse(thread);
        var json = JsonSerializer.Serialize(new { update_id = 1, message });
        await (Task)typeof(TelegramUpdateService).GetMethod("DispatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(update, [TelegramUpdate.Parse(json)!, CancellationToken.None])!;
    }

    /// <summary>The session pods' terminals: records each typed input frame under the pod it reached.</summary>
    private static async Task ServePodsAsync(HttpListener listener, Dictionary<string, List<string>> byIp, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(stop); }
            catch (OperationCanceledException) { break; }
            if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 404; ctx.Response.Close(); continue; }
            var typed = byIp[ctx.Request.LocalEndPoint.Address.ToString()];
            using var socket = (await ctx.AcceptWebSocketAsync("tty")).WebSocket;
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open)
            {
                var frame = await socket.ReceiveAsync(buffer, stop);
                if (frame.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", stop);
                    break;
                }
                using var json = JsonDocument.Parse(buffer.AsMemory(0, frame.Count));
                lock (typed) typed.Add(json.RootElement.GetProperty("data").GetString()!);
            }
        }
    }

    /// <summary>
    /// Bot API recorder (distinct message ids and topic ids, so reply routing and topics are
    /// real) and the Kubernetes API stand-in of the deleted-session scenario on one port.
    /// </summary>
    private static async Task ServeBotAsync(HttpListener listener, List<string> wire, CancellationToken stop)
    {
        var nextId = 100;
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().WaitAsync(stop); }
            catch (OperationCanceledException) { break; }
            var path = ctx.Request.Url!.AbsolutePath;
            string body;
            if (path.StartsWith("/bot", StringComparison.Ordinal))
            {
                using var json = await JsonDocument.ParseAsync(ctx.Request.InputStream);
                var method = ctx.Request.Url.Segments.Last();
                var thread = json.RootElement.TryGetProperty("message_thread_id", out var t) ? $" [topic {t}]" : "";
                var text = json.RootElement.TryGetProperty("text", out var x) ? x.GetString()
                    : json.RootElement.TryGetProperty("name", out var n) ? n.GetString() : "";
                lock (wire) wire.Add($"{method}{thread}: {text}");
                var id = Interlocked.Increment(ref nextId);
                body = method == "createForumTopic"
                    ? $"{{\"ok\":true,\"result\":{{\"message_thread_id\":{id}}}}}"
                    : $"{{\"ok\":true,\"result\":{{\"message_id\":{id}}}}}";
            }
            else if (ctx.Request.HttpMethod == "DELETE")
                body = "{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"status\":\"Success\"}";
            else if (path.EndsWith("/pods", StringComparison.Ordinal) || path.EndsWith("/jobs", StringComparison.Ordinal)
                     || path.EndsWith("/secrets", StringComparison.Ordinal) || path.EndsWith("/cronjobs", StringComparison.Ordinal))
                body = "{\"apiVersion\":\"v1\",\"kind\":\"List\",\"items\":[]}";
            else
            {
                ctx.Response.StatusCode = 404;
                body = "{\"kind\":\"Status\",\"apiVersion\":\"v1\",\"status\":\"Failure\",\"code\":404}";
            }
            ctx.Response.ContentType = "application/json";
            await using var output = ctx.Response.OutputStream;
            await output.WriteAsync(Encoding.UTF8.GetBytes(body), stop);
        }
    }
}
