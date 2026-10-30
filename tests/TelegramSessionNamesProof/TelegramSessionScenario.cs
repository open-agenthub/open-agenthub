using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using AgentHub.Api.Browser;
using System.Reflection;
using System.Text.Json;
using AgentHub.Api.Chat;
using AgentHub.Api.Chat.Telegram;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace TelegramSessionNamesProof;

public record WireMessage(string Method, JsonElement Body);

public class SessionLookup : DispatchProxy
{
    public string? Title = "Build <b>& fix</b>_*";
    public bool Running;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var info = new SessionInfo { Id = "abcd1234", Owner = "owner", Title = Title!,
            Mode = SessionMode.Interactive, Phase = Running ? "Running" : "Stopped", PodIp = Running ? "127.0.0.1" : null };
        return method!.Name switch
        {
            nameof(ISessionService.GetSessionAsync) => Task.FromResult<SessionInfo?>(info),
            nameof(ISessionService.CreateSessionAsync) => Task.FromResult(info),
            nameof(ISessionService.TouchActivityAsync) => Task.CompletedTask,
            _ => throw new NotSupportedException(method.Name)
        };
    }
}

// Only the transport destination changes; the production client still serializes every Bot API request.
public sealed class LocalBotFactory(Uri address) : IHttpClientFactory, IDisposable
{
    private sealed class Redirect : HttpMessageHandler
    {
        private readonly Uri _address;
        private readonly HttpClientHandler _handler = new();
        private readonly HttpMessageInvoker _transport;
        public Redirect(Uri address)
        {
            _address = address;
            _transport = new HttpMessageInvoker(_handler, disposeHandler: false);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _transport.Dispose(); _handler.Dispose(); }
            base.Dispose(disposing);
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new Uri(_address, request.RequestUri!.AbsolutePath);
            return _transport.SendAsync(request, ct);
        }
    }
    private readonly HttpClient _client = new(new Redirect(address));
    public HttpClient CreateClient(string name) => _client;
    public void Dispose() => _client.Dispose();
}

public sealed class LocalPod : IAgentPodIdentityResolver
{
    public Task<bool> IsLiveSessionPodAsync(string id, IPAddress ip, CancellationToken ct = default)
        => Task.FromResult(true);
}

public static class TelegramSessionScenario
{
    public static readonly string[] Paths = ["question", "finished", "failed", "session-expired", "permission",
        "expired", "decided", "callback", "callback-expired", "sessions", "use", "status", "new", "delivery-error", "indicator", "fallback", "forum", "new-fallback", "use-unknown", "use-ambiguous", "use-other", "long-title"];

    public static async Task<List<WireMessage>> RunAsync(string path, string connectionString, bool verify = true)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var wire = new List<WireMessage>();
        using var stop = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().WaitAsync(stop.Token); }
                catch (OperationCanceledException) { break; }
                if (ctx.Request.IsWebSocketRequest)
                {
                    var socket = (await ctx.AcceptWebSocketAsync("tty")).WebSocket;
                    var buffer = new byte[4096];
                    while (socket.State == WebSocketState.Open)
                    {
                        var frame = await socket.ReceiveAsync(buffer, stop.Token);
                        if (frame.MessageType == WebSocketMessageType.Close)
                        { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", stop.Token); break; }
                    }
                    socket.Dispose();
                    continue;
                }
                using var body = await JsonDocument.ParseAsync(ctx.Request.InputStream);
                var method = ctx.Request.Url!.Segments.Last();
                lock (wire) wire.Add(new(method, body.RootElement.Clone()));
                var result = method == "createForumTopic" ? "{\"message_thread_id\":77}" : "{\"message_id\":42}";
                if (path == "new-fallback")
                {
                    lock (wire) { if (wire.Count == 1) { ctx.Response.StatusCode = 500; result = "null"; } }
                }
                ctx.Response.ContentType = "application/json";
                await using var output = ctx.Response.OutputStream;
                await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"ok\":" + (result == "null" ? "false" : "true") + ",\"result\":" + result + "}"));
            }
        });
        var schema = "tg_" + Guid.NewGuid().ToString("N");
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"CREATE SCHEMA {schema}", db)) await cmd.ExecuteNonQueryAsync();
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ToString();
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Postgres"] = cs, ["FrontendOrigin"] = "https://example.com", ["AgentHub:AgentPort"] = port.ToString() }).Build();
            var users = new UserDirectory(cfg); await users.InitializeAsync();
            await users.RecordLoginAsync("owner", null, null);
            await users.SetTelegramLinkAsync("owner", "5", path == "forum");
            var bindings = new ChatBindingStore(cfg); await bindings.InitializeAsync();
            await bindings.UpsertAsync(new("telegram", "abcd1234", "owner", "5", null, null, false));
            await bindings.SetActiveAsync("telegram", "5", "abcd1234");
            var permissions = new PermissionStore(cfg); await permissions.InitializeAsync();
            var opts = new TelegramOptions { Enabled = true, BotToken = "stand-in" };
            using var factory = new LocalBotFactory(new Uri($"http://127.0.0.1:{port}/"));
            var tg = new TelegramClient(factory, opts, NullLogger<TelegramClient>.Instance);
            var conversations = new TelegramConversationFactory(tg, bindings, users, cfg, NullLogger<TelegramConversationFactory>.Instance);
            var sessions = DispatchProxy.Create<ISessionService, SessionLookup>();
            var lookup = (SessionLookup)sessions;
            if (path == "fallback") lookup.Title = " ";
            if (path == "long-title") lookup.Title = "Title\0\n" + new string('x', 200) + "😀";
            if (path == "use-ambiguous")
                await bindings.UpsertAsync(new("telegram", "abcd9999", "owner", "5", null, null, false));
            if (path == "use-other")
            {
                await bindings.UpsertAsync(new("telegram", "abcd1234", "previous-owner", "5", null, null, false));
            }
            var indicator = new WorkingIndicator(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));
            var notifier = new TelegramNotifier(opts, tg, bindings, users, conversations, indicator, NullLogger<TelegramNotifier>.Instance);
            var editor = new TelegramPermissionNotifier(opts, tg, bindings, users, permissions, sessions);
            var update = new TelegramUpdateService(opts, tg, bindings, new ChatLinkCodeStore(cfg), users,
                permissions, sessions, conversations, new ChatRepoService(null!, NullLogger<ChatRepoService>.Instance),
                indicator, new AgentPodRequests(new HttpClient(), new LocalPod()), cfg, NullLogger<TelegramUpdateService>.Instance);
            var req = new PermissionRequest { Id = "request", SessionId = "abcd1234", Owner = "owner", Tool = "Read", Summary = "file", Channel = "5", MessageTs = "42" };
            await permissions.CreateAsync(req);
            async Task Dispatch(string text, bool callback = false)
            {
                var json = callback
                    ? "{\"update_id\":1,\"callback_query\":{\"id\":\"cb\",\"data\":\"perm:allow:request\",\"from\":{\"id\":1},\"message\":{\"message_id\":42,\"chat\":{\"id\":5}}}}"
                    : JsonSerializer.Serialize(new { update_id = 1, message = new { message_id = 1, chat = new { id = 5, type = "private" }, from = new { id = 1, is_bot = false }, text } });
                var task = (Task)typeof(TelegramUpdateService).GetMethod("DispatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(update, [TelegramUpdate.Parse(json)!, CancellationToken.None])!;
                await task;
            }
            switch (path)
            {
                case "question": case "finished": case "failed": case "session-expired":
                    await notifier.NotifyAsync(new SessionRecord { Id = "abcd1234", Owner = "owner", Title = lookup.Title!, CallbackToken = "unused" }, path,
                        path == "question" ? new string('z', 8100) : "complete"); break;
                case "permission": case "fallback": case "long-title":
                    await PermissionRelay.TryPostAsync([editor], req, default); break;
                case "expired": await editor.MarkExpiredAsync(req); break;
                case "decided": await editor.MarkDecidedAsync(req, "allowAlways"); break;
                case "callback": await Dispatch("", true); break;
                case "callback-expired": await permissions.ResolveAsync(req.Id, "expired"); await Dispatch("", true); break;
                case "use-unknown": case "use-ambiguous": case "use-other": case "forum": case "new-fallback": case "sessions": case "use": case "status": case "new":
                    await Dispatch(path switch { "use" or "use-ambiguous" or "use-other" => "/use abcd", "use-unknown" => "/use ffff", "new" or "forum" or "new-fallback" => "/new fix it", _ => "/" + path }); break;
                case "delivery-error": await Dispatch("hello"); break;
                case "indicator":
                    lookup.Running = true;
                    await Dispatch("hello");
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < deadline)
                    {
                        lock (wire) { if (wire.Any(w => w.Method == "editMessageText")) break; }
                        await Task.Delay(10);
                    }
                    indicator.Stop("abcd1234");
                    break;
                default: throw new ArgumentException(path);
            }
            if (verify)
            {
                if (wire.Count == 0) throw new Exception("No Bot API calls for " + path);
                foreach (var item in wire.Where(w => w.Body.TryGetProperty("text", out _)))
                {
                    var text = item.Body.GetProperty("text").GetString()!;
                    var expected = path switch
                    {
                        "fallback" => "#abcd",
                        "long-title" => ChatFormatting.SessionLabel("abcd1234", lookup.Title),
                        "use-unknown" or "use-other" => "No session matches that tag.",
                        "use-ambiguous" => "Ambiguous — be more specific.",
                        _ => lookup.Title!
                    };
                    if (!text.Contains(expected)) throw new Exception(path + " missing origin: " + text);
                    if (text.Length > 4096 || item.Body.TryGetProperty("parse_mode", out _)) throw new Exception("Unsafe wire formatting");
                }
                if (path == "long-title" && wire[0].Body.GetProperty("text").GetString()!.Split('\n')[0].Length > 95)
                    throw new Exception("Uncapped title");
                if (path == "question" && wire.Count < 3) throw new Exception("No continuation proof");
                if (path == "indicator" && !wire.Any(w => w.Method == "editMessageText")) throw new Exception("No animation edit");
            }
            return wire;
        }
        finally
        {
            stop.Cancel(); await server;
            await using var cmd = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", db); await cmd.ExecuteNonQueryAsync();
        }
    }
}
