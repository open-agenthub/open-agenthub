using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Browser;
using AgentHub.Api.Chat;
using AgentHub.Api.Chat.Telegram;
using AgentHub.Api.Library;
using AgentHub.Api.Models;
using AgentHub.Api.Notifications;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace TelegramSessionNamesProof;

/// <summary>Answers every call with its type's default, for dependencies the delete and list paths never use.</summary>
public class Unused : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var type = method!.ReturnType;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var inner = type.GenericTypeArguments[0];
            object? value = inner.IsValueType ? Activator.CreateInstance(inner)
                : inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                    ? Array.CreateInstance(inner.GenericTypeArguments[0], 0) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, [value]);
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>No browser was ever started for these sessions.</summary>
public class NoBrowsers : Unused
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        nameof(IBrowserService.GetSummaryAsync) => Task.FromResult(BrowserSummary.Stopped),
        nameof(IBrowserService.GetSummariesAsync) => Task.FromResult<IReadOnlyDictionary<string, BrowserSummary>>(
            ((IReadOnlyCollection<string>)args![0]!).ToDictionary(id => id, _ => BrowserSummary.Stopped)),
        _ => base.Invoke(method, args)
    };
}

/// <summary>One step of the deleted-session run: what was sent and what went over the wire.</summary>
public record DeletedStep(string Step, string? Sent, List<string> Replies, Dictionary<string, long> Rows, string? Result = null);

/// <summary>
/// #365 end to end: the production Telegram router and notifier, binding store, session store,
/// expiry sweep and the real <see cref="KubernetesSessionService"/> delete, on Postgres, with
/// the Bot API and the Kubernetes API answered by loopback stand-ins. Only those two transports
/// are substituted; the delete runs the same method the REST, MCP and internal paths call.
/// </summary>
public static class DeletedSessionScenario
{
    private const string Owner = "owner";
    private const string Chat = "5";

    public static async Task<List<DeletedStep>> RunAsync(string connectionString, bool verify = true)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var wire = new List<string>();
        using var stop = new CancellationTokenSource();
        var server = Task.Run(() => ServeAsync(listener, wire, stop.Token));

        var schema = "tgdel_" + Guid.NewGuid().ToString("N");
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync();
        await using (var cmd = new NpgsqlCommand($"CREATE SCHEMA {schema}", db)) await cmd.ExecuteNonQueryAsync();
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema }.ToString();
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = cs, ["FrontendOrigin"] = "https://example.com",
                ["AgentHub:Namespace"] = "proof", ["AgentHub:SkillLibraryMcp"] = "false"
            }).Build();
            var store = new PostgresSessionStore(cfg); await store.InitializeAsync();
            var users = new UserDirectory(cfg); await users.InitializeAsync();
            await users.RecordLoginAsync(Owner, null, null);
            await users.SetTelegramLinkAsync(Owner, Chat, true);
            var bindings = new ChatBindingStore(cfg); await bindings.InitializeAsync();
            var permissions = new PermissionStore(cfg); await permissions.InitializeAsync();
            var codes = new ChatLinkCodeStore(cfg); await codes.InitializeAsync();
            var raw = NpgsqlDataSource.Create(cs);

            var start = DateTime.UtcNow.AddHours(-30);
            async Task<SessionRecord> Session(string id, string title, int minutesOld, bool expiring = false)
            {
                var record = new SessionRecord
                {
                    Id = id, Owner = Owner, Title = title, Mode = SessionMode.Interactive, CallbackToken = "cb-" + id,
                    CreatedAt = start, LastActivityAt = DateTime.UtcNow.AddMinutes(-minutesOld),
                    AutoDeleteAfterSeconds = expiring ? 60 : null, AutoDeleteFrom = expiring ? SessionExpiry.FromStart : null
                };
                await store.UpsertAsync(record);
                return record;
            }
            // Seventeen live sessions bound to the chat, one of them about to be deleted and the
            // active one, one in a forum topic due for auto-delete; plus three bindings that a
            // delete before this change left behind.
            for (var i = 0; i < 15; i++)
            {
                await Session($"live{i:00}aa", $"Worker {i:00}", 120 + i);
                await bindings.UpsertAsync(new("telegram", $"live{i:00}aa", Owner, Chat, null, null, false));
            }
            await Session("dele7e01", "Release notes", 5);
            await bindings.UpsertAsync(new("telegram", "dele7e01", Owner, Chat, null, null, false));
            await bindings.RecordMessageAsync("telegram", Chat, "900", "dele7e01");
            await bindings.SetActiveAsync("telegram", Chat, "dele7e01");
            await Session("expi4e01", "Nightly check", 1, expiring: true);
            await bindings.UpsertAsync(new("telegram", "expi4e01", Owner, Chat, "77", null, false));
            foreach (var gone in new[] { "e8d5aaaa", "f00dbbbb", "c0decccc" })
            {
                await bindings.UpsertAsync(new("telegram", gone, Owner, Chat, null, null, false));
                await bindings.RecordMessageAsync("telegram", Chat, "m-" + gone, gone);
            }

            using var cluster = new k8s.Kubernetes(new k8s.KubernetesClientConfiguration { Host = $"http://127.0.0.1:{port}/" });
            var locks = new PostgresSessionExpiryLock(cfg);
            var sessions = new KubernetesSessionService(cfg, store, DispatchProxy.Create<IProjectStore, Unused>(), new NullArtifactStore(),
                DispatchProxy.Create<IBrowserService, NoBrowsers>(), DispatchProxy.Create<IGitAuthService, Unused>(), null!,
                new AgentHub.Api.Agents.AllowAllAgentsProvider(), DispatchProxy.Create<ILibraryAccess, Unused>(),
                DispatchProxy.Create<IMcpServerStore, Unused>(), new InMemoryEphemeralApiMcpStore(), null!,
                NullLogger<KubernetesSessionService>.Instance, runtimeUpdateLock: locks, cluster: cluster, chatCleanup: bindings);

            var opts = new TelegramOptions { Enabled = true, BotToken = "stand-in" };
            using var factory = new LocalBotFactory(new Uri($"http://127.0.0.1:{port}/"));
            var tg = new TelegramClient(factory, opts, NullLogger<TelegramClient>.Instance);
            var conversations = new TelegramConversationFactory(tg, bindings, users, cfg, NullLogger<TelegramConversationFactory>.Instance);
            var indicator = new WorkingIndicator(TimeSpan.FromMilliseconds(10), TimeSpan.FromSeconds(30));
            var notifier = new TelegramNotifier(opts, tg, bindings, users, conversations, indicator, NullLogger<TelegramNotifier>.Instance);
            var update = new TelegramUpdateService(opts, tg, bindings, codes, users, permissions, sessions, conversations,
                new ChatRepoService(null!, NullLogger<ChatRepoService>.Instance), indicator,
                new AgentPodRequests(new HttpClient(), new LocalPod()), cfg, NullLogger<TelegramUpdateService>.Instance);
            var sweep = new SessionExpirySweepService(store, sessions, locks, new INotifier[] { notifier },
                NullLogger<SessionExpirySweepService>.Instance);

            var steps = new List<DeletedStep>();
            async Task<Dictionary<string, long>> Rows()
            {
                var rows = new Dictionary<string, long>();
                foreach (var table in new[] { "chat_session_bindings", "chat_messages", "chat_deleted_sessions", "sessions" })
                {
                    // -1: the table does not exist (the baseline revision has no deleted-session markers).
                    await using var exists = raw.CreateCommand($"SELECT to_regclass('{table}') IS NOT NULL");
                    if (await exists.ExecuteScalarAsync() is not true) { rows[table] = -1; continue; }
                    await using var count = raw.CreateCommand($"SELECT count(*) FROM {table}");
                    rows[table] = (long)(await count.ExecuteScalarAsync())!;
                }
                return rows;
            }
            async Task Step(string name, string? text, Func<Task<string?>>? action = null, string? thread = null)
            {
                int before;
                lock (wire) before = wire.Count;
                if (text is not null) await DispatchAsync(update, text, thread);
                var result = action is null ? null : await action();
                List<string> replies;
                lock (wire) replies = wire.Skip(before).Where(w => w.StartsWith("sendMessage", StringComparison.Ordinal)).ToList();
                steps.Add(new(name, text, replies, await Rows(), result));
            }

            await Step("1 /sessions before the upgrade's prune (3 bindings of long-deleted sessions in the table)", "/sessions");
            await Step("2 poll-service start: PruneAsync", null, async () => $"{await bindings.PruneAsync()} stale bindings removed");
            await Step("3 /status of the active session", "/status");
            await Step("4 delete the active session through KubernetesSessionService.DeleteSessionAsync", null,
                async () => { await sessions.DeleteSessionAsync(Owner, "dele7e01"); return null; });
            await Step("5 /sessions after the delete", "/sessions");
            await Step("6 plain message after the delete", "please continue");
            await Step("7 /status after the delete", "/status");
            await Step("8 /use with the deleted tag", "/use dele");
            await Step("9 expiry sweep deletes the forum-topic session", null,
                async () => $"{await sweep.SweepOnceAsync(DateTime.UtcNow, CancellationToken.None)} session expired");
            await Step("10 message in the deleted session's topic", "still there?", thread: "77");
            await Step("11 /use another session", "/use live00");
            await Step("12 plain message once another session is active", "hello");

            if (verify) Verify(steps);
            return steps;
        }
        finally
        {
            stop.Cancel();
            await server;
            await using var cmd = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", db);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public static void Verify(List<DeletedStep> steps)
    {
        Check(steps[1].Result == "3 stale bindings removed", "prune removes the three stale bindings");
        Check(steps[8].Result == "1 session expired", "the sweep expires the topic session");
        Check(Has(steps[0], "Release notes (#dele) · Pending · active") && !Has(steps[0], "Unknown"), "list shows the active session and no Unknown rows");
        Check(Has(steps[0], "… and 2 older. /use <tag> switches to any of them."), "list is capped at 15 with a hint");
        Check(steps[0].Replies.Single().IndexOf("Nightly check", StringComparison.Ordinal)
              < steps[0].Replies.Single().IndexOf("Release notes", StringComparison.Ordinal), "most recently used first");
        Check(steps[1].Rows["chat_session_bindings"] == 17 && steps[1].Rows["chat_deleted_sessions"] == 3, "prune leaves only live bindings");
        Check(steps[3].Rows["chat_session_bindings"] == 16 && steps[3].Rows["chat_messages"] == 0 && steps[3].Rows["sessions"] == 16,
            "delete removes the session's binding and reply routing");
        Check(!Has(steps[4], "Release notes"), "deleted session is gone from /sessions");
        Check(Has(steps[5], "🗑 The active session Release notes (#dele) was deleted. /sessions lists the remaining ones, /use <tag> picks one."), "plain message answer");
        Check(Has(steps[6], "🗑 The active session Release notes (#dele)"), "status answer");
        Check(Has(steps[7], "No session matches that tag."), "deleted tag cannot be reselected");
        Check(Has(steps[8], "🏁 session-expired — The session was deleted automatically") && steps[8].Rows["chat_session_bindings"] == 15,
            "expiry notice still reaches the topic after its binding is gone");
        Check(Has(steps[9], "🗑 Nightly check (#expi) was deleted. This topic stays as its history."), "topic answer");
        Check(Has(steps[10], "Plain replies now go to Worker 00"), "switch");
        Check(Has(steps[11], "Session is not running") && !Has(steps[11], "🗑"), "a new choice retires the deleted answer");
    }

    private static bool Has(DeletedStep step, string text) => step.Replies.Any(r => r.Contains(text, StringComparison.Ordinal));

    private static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("Deleted-session proof failed: " + what);
    }

    private static async Task DispatchAsync(TelegramUpdateService update, string text, string? thread)
    {
        var message = new Dictionary<string, object?>
        {
            ["message_id"] = 1, ["chat"] = new { id = 5, type = "supergroup", is_forum = true },
            ["from"] = new { id = 1, is_bot = false }, ["text"] = text
        };
        if (thread is not null) message["message_thread_id"] = int.Parse(thread);
        var json = JsonSerializer.Serialize(new { update_id = 1, message });
        await (Task)typeof(TelegramUpdateService).GetMethod("DispatchAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(update, [TelegramUpdate.Parse(json)!, CancellationToken.None])!;
    }

    /// <summary>
    /// Bot API recorder and Kubernetes API stand-in on one port. The cluster has nothing left of
    /// the sessions: lists are empty, single reads 404 and deletes succeed — what a pod that
    /// already ended looks like to the delete path.
    /// </summary>
    private static async Task ServeAsync(HttpListener listener, List<string> wire, CancellationToken stop)
    {
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
                var text = json.RootElement.TryGetProperty("text", out var x) ? x.GetString() : "";
                lock (wire) wire.Add($"{method}{thread}: {text}");
                body = "{\"ok\":true,\"result\":{\"message_id\":42}}";
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
