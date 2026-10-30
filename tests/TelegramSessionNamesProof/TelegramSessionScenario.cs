using System.Net;
using System.Net.Sockets;
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
public sealed class LocalBotFactory(Uri address) : IHttpClientFactory
{
    private sealed class Redirect(Uri address) : DelegatingHandler(new HttpClientHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri = new Uri(address, request.RequestUri!.AbsolutePath);
            return base.SendAsync(request, ct);
        }
    }
    public HttpClient CreateClient(string name) => new(new Redirect(address));
}

public static class TelegramSessionScenario
{
    public static readonly string[] Paths = ["question", "finished", "failed", "session-expired", "permission",
        "expired", "decided", "callback", "callback-expired", "sessions", "use", "status", "new", "delivery-error", "indicator", "fallback"];

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
                var body = await JsonDocument.ParseAsync(ctx.Request.InputStream);
                var method = ctx.Request.Url!.Segments.Last();
                lock (wire) wire.Add(new(method, body.RootElement.Clone()));
                var result = method == "createForumTopic" ? "{\"message_thread_id\":77}" : "{\"message_id\":42}";
                ctx.Response.ContentType = "application/json";
                await using var output = ctx.Response.OutputStream;
                await output.WriteAsync(System.Text.Encoding.UTF8.GetBytes("{\"ok\":true,\"result\":" + result + "}"));
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
                { ["ConnectionStrings:Postgres"] = cs, ["FrontendOrigin"] = "https://example.com" }).Build();
            var users = new UserDirectory(cfg); await users.InitializeAsync();
            await users.RecordLoginAsync("owner", null, null);
            await users.SetTelegramLinkAsync("owner", "5", false);
            var bindings = new ChatBindingStore(cfg); await bindings.InitializeAsync();
            await bindings.UpsertAsync(new("telegram", "abcd1234", "owner", "5", null, null, false));
            await bindings.SetActiveAsync("telegram", "5", "abcd1234");
            var permissions = new PermissionStore(cfg); await permissions.InitializeAsync();
            var opts = new TelegramOptions { Enabled = true, BotToken = "stand-in" };
            var tg = new TelegramClient(new LocalBotFactory(new Uri($"http://127.0.0.1:{port}/")), opts, NullLogger<TelegramClient>.Instance);
            var conversations = new TelegramConversationFactory(tg, bindings, users, cfg, NullLogger<TelegramConversationFactory>.Instance);
            var sessions = DispatchProxy.Create<ISessionService, SessionLookup>();
            var lookup = (SessionLookup)sessions;
            if (path == "fallback") lookup.Title = " ";
            var indicator = new WorkingIndicator(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(1));
            var notifier = new TelegramNotifier(opts, tg, bindings, users, conversations, indicator, NullLogger<TelegramNotifier>.Instance);
            var editor = new TelegramPermissionNotifier(opts, tg, bindings, users, permissions, sessions);
            var update = new TelegramUpdateService(opts, tg, bindings, new ChatLinkCodeStore(cfg), users,
                permissions, sessions, conversations, new ChatRepoService(null!, NullLogger<ChatRepoService>.Instance),
                indicator, null!, cfg, NullLogger<TelegramUpdateService>.Instance);
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
                case "permission": case "fallback":
                    await PermissionRelay.TryPostAsync([editor], req, default); break;
                case "expired": await editor.MarkExpiredAsync(req); break;
                case "decided": await editor.MarkDecidedAsync(req, "allowAlways"); break;
                case "callback": await Dispatch("", true); break;
                case "callback-expired": await permissions.ResolveAsync(req.Id, "expired"); await Dispatch("", true); break;
                case "sessions": case "use": case "status": case "new":
                    await Dispatch(path switch { "use" => "/use abcd", "new" => "/new fix it", _ => "/" + path }); break;
                case "delivery-error": await Dispatch("hello"); break;
                case "indicator":
                    // The production animator calls the same edit closure used after a terminal reply.
                    var frames = WorkingIndicator.FramesFor(null).Select(f => ChatFormatting.Header("abcd1234", lookup.Title) + "\n" + f).ToArray();
                    await tg.SendMessageAsync("5", frames[0], null, null, default);
                    indicator.Start("abcd1234", (t, c) => tg.EditMessageTextAsync("5", "42", t, null, c), frames);
                    await Task.Delay(70); indicator.Stop("abcd1234"); break;
                default: throw new ArgumentException(path);
            }
            if (verify)
            {
                if (wire.Count == 0) throw new Exception("No Bot API calls for " + path);
                foreach (var item in wire.Where(w => w.Body.TryGetProperty("text", out _)))
                {
                    var text = item.Body.GetProperty("text").GetString()!;
                    var expected = path == "fallback" ? "#abcd" : lookup.Title!;
                    if (!text.Contains(expected)) throw new Exception(path + " missing origin: " + text);
                    if (text.Length > 4096 || item.Body.TryGetProperty("parse_mode", out _)) throw new Exception("Unsafe wire formatting");
                }
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
