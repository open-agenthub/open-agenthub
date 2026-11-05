using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Chat;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Ee.Slack;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Who may drive a session from its Slack thread (#436). Runs the real Socket Mode envelope
/// handling over real Postgres stores (threads, permission requests, users, shares) with a
/// recording stand-in for the Slack Web API, so every assertion is about what reached the
/// session and what was posted back.
/// </summary>
public class SlackThreadSenderPostgresTests
{
    private const string Channel = "C0CHANNEL";
    private const string ThreadTs = "1700000000.000100";
    private const string Secret = "deploy with password hunter2";

    [PostgreSqlFact]
    public async Task OwnerReplyReachesTheSession()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.TextAsync("U_ALICE", Secret);
        Assert.Equal(["s1"], rig.Pods.Confirmed);
        Assert.Empty(rig.Slack.Ephemerals);
    }

    [PostgreSqlFact]
    public async Task OwnerStatusIsAnswered()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.TextAsync("U_ALICE", "!status");
        Assert.Contains(rig.Slack.Posts, p => p.Method == "chat.postMessage" && p.Thread == ThreadTs);
    }

    [PostgreSqlFact]
    public async Task OtherChannelMemberIsRefusedOnceWithoutEcho()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.TextAsync("U_MALLORY", Secret);
        await rig.TextAsync("U_MALLORY", Secret + " again");
        await rig.TextAsync("U_MALLORY", "!status");

        Assert.Empty(rig.Pods.Confirmed);
        var notice = Assert.Single(rig.Slack.Ephemerals);
        Assert.Equal("U_MALLORY", notice.User);
        Assert.Equal(SlackSenderGate.RefusedNotice, notice.Text);
        Assert.Equal(ThreadTs, notice.Thread);
        // Nothing public at all: no echo, no status, no working indicator.
        Assert.DoesNotContain(rig.Slack.Posts, p => p.Method == "chat.postMessage");
        Assert.DoesNotContain(rig.Slack.Posts, p => p.Body.Contains("hunter2"));
    }

    [PostgreSqlFact]
    public async Task UnlinkedSlackUserIsRefusedAndToldOnceHowToLink()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.TextAsync("U_STRANGER", Secret);
        await rig.TextAsync("U_STRANGER", Secret);
        await rig.ClickAsync("U_STRANGER", "allow");

        Assert.Empty(rig.Pods.Confirmed);
        Assert.Null(await rig.DecisionAsync());
        var notice = Assert.Single(rig.Slack.Ephemerals);
        Assert.Equal(SlackSenderGate.UnlinkedNotice, notice.Text);
        Assert.DoesNotContain(rig.Slack.Posts, p => p.Body.Contains("hunter2"));
    }

    [PostgreSqlFact]
    public async Task SlackUserWithoutEmailIsUnlinked()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.TextAsync("U_NOEMAIL", Secret);
        Assert.Empty(rig.Pods.Confirmed);
        Assert.Equal(SlackSenderGate.UnlinkedNotice, Assert.Single(rig.Slack.Ephemerals).Text);
    }

    [PostgreSqlFact]
    public async Task AmbiguousEmailIsUnlinkedEvenForTheOwnersAddress()
    {
        await using var rig = await Rig.CreateAsync();
        // A second account with the owner's address makes the mapping ambiguous; the hub
        // refuses to guess which of the two is speaking.
        await rig.Users.RecordLoginAsync("alice-twin", "alice@example.test", "twin");
        await rig.TextAsync("U_ALICE", Secret);
        Assert.Empty(rig.Pods.Confirmed);
        Assert.Equal(SlackSenderGate.UnlinkedNotice, Assert.Single(rig.Slack.Ephemerals).Text);
    }

    [PostgreSqlFact]
    public async Task ViewerIsRefused()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Db.Shares.UpsertDirectAsync("alice", "s1", "vic", ShareRole.Viewer);
        await rig.TextAsync("U_VIC", Secret);
        foreach (var decision in Decisions) await rig.ClickAsync("U_VIC", decision);

        Assert.Empty(rig.Pods.Confirmed);
        Assert.Null(await rig.DecisionAsync());
        Assert.Equal(SlackSenderGate.RefusedNotice, Assert.Single(rig.Slack.Ephemerals).Text);
    }

    [PostgreSqlFact]
    public async Task CollaboratorReplyAndClickAreAccepted()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Db.Shares.UpsertDirectAsync("alice", "s1", "bob", ShareRole.Collaborator);
        await rig.TextAsync("U_BOB", Secret);
        await rig.ClickAsync("U_BOB", "deny");

        Assert.Equal(["s1"], rig.Pods.Confirmed);
        Assert.Equal("deny", await rig.DecisionAsync());
        Assert.Contains(rig.Slack.Posts, p => p.Method == "chat.update" && p.Body.Contains("Denied"));
        Assert.Empty(rig.Slack.Ephemerals);
    }

    [PostgreSqlFact]
    public async Task CollaboratorIsRefusedWithoutAnEnterpriseLicense()
    {
        await using var rig = await Rig.CreateAsync(licensed: false);
        await rig.Db.Shares.UpsertDirectAsync("alice", "s1", "bob", ShareRole.Collaborator);
        await rig.TextAsync("U_BOB", Secret);
        await rig.ClickAsync("U_BOB", "allow");
        Assert.Empty(rig.Pods.Confirmed);
        Assert.Null(await rig.DecisionAsync());
    }

    [PostgreSqlFact]
    public async Task RevokedCollaboratorIsRefusedAtOnce()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Db.Shares.UpsertDirectAsync("alice", "s1", "bob", ShareRole.Collaborator);
        await rig.TextAsync("U_BOB", "first");
        await rig.Db.Shares.DeleteDirectAsync("alice", "s1", "bob");
        await rig.TextAsync("U_BOB", Secret);
        Assert.Equal(["s1"], rig.Pods.Confirmed);
    }

    public static readonly string[] Decisions = ["allow", "allowAlways", "deny"];

    [PostgreSqlTheory]
    [InlineData("allow")]
    [InlineData("allowAlways")]
    [InlineData("deny")]
    public async Task OtherChannelMemberCannotPressAnyPermissionButton(string decision)
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ClickAsync("U_MALLORY", decision);

        Assert.Null(await rig.DecisionAsync());
        Assert.False(await rig.Permissions.IsAlwaysAllowedAsync("s1", "Bash"));
        // The owner's prompt keeps its buttons: no chat.update, not even "Already decided".
        Assert.DoesNotContain(rig.Slack.Posts, p => p.Method == "chat.update");
        Assert.Equal(SlackSenderGate.RefusedNotice, Assert.Single(rig.Slack.Ephemerals).Text);
    }

    [PostgreSqlTheory]
    [InlineData("allow", "Allowed")]
    [InlineData("allowAlways", "Allowed")]
    [InlineData("deny", "Denied")]
    public async Task OwnerCanPressEveryPermissionButton(string decision, string shown)
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ClickAsync("U_ALICE", decision);
        Assert.Equal(decision, await rig.DecisionAsync());
        Assert.Equal(decision == "allowAlways", await rig.Permissions.IsAlwaysAllowedAsync("s1", "Bash"));
        Assert.Contains(rig.Slack.Posts, p => p.Method == "chat.update" && p.Body.Contains(shown));
    }

    [PostgreSqlFact]
    public async Task RefusedClickOnADecidedRequestDoesNotRewriteThePrompt()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ClickAsync("U_ALICE", "allow");
        rig.Slack.Clear();
        await rig.ClickAsync("U_MALLORY", "deny");
        Assert.Equal("allow", await rig.DecisionAsync());
        Assert.DoesNotContain(rig.Slack.Posts, p => p.Method == "chat.update");
    }

    [PostgreSqlFact]
    public async Task ClickWithoutAUserIsIgnoredSilently()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ClickAsync(null, "allow");
        Assert.Null(await rig.DecisionAsync());
        Assert.Empty(rig.Slack.Posts);
    }

    // Same-owner regressions (#421's lesson): the owner's own paths must not start to depend
    // on sharing, the licence state of sharing or on notification preferences.

    [PostgreSqlFact]
    public async Task OwnerIsAcceptedWithoutLicenseOrShares()
    {
        await using var rig = await Rig.CreateAsync(licensed: false);
        await rig.TextAsync("U_ALICE", Secret);
        await rig.ClickAsync("U_ALICE", "allow");
        Assert.Equal(["s1"], rig.Pods.Confirmed);
        Assert.Equal("allow", await rig.DecisionAsync());
    }

    [PostgreSqlFact]
    public async Task OwnerWhoSwitchedSlackNotificationsOffCanStillAnswerTheirThread()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Users.SetSlackPrefsAsync("alice", false, null);
        await rig.TextAsync("U_ALICE", Secret);
        Assert.Equal(["s1"], rig.Pods.Confirmed);
    }

    [PostgreSqlFact]
    public async Task OwnerInTheirDirectMessageThreadIsAccepted()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Db.AddSessionAsync("alice", "s2");
        await rig.Threads.UpsertAsync(new SlackThread("s2", "alice", "D0ALICE", "1700000000.000200"));
        await rig.TextAsync("U_ALICE", Secret, channel: "D0ALICE", threadTs: "1700000000.000200");
        Assert.Equal(["s2"], rig.Pods.Confirmed);
        Assert.Empty(rig.Slack.Ephemerals);
    }

    [PostgreSqlFact]
    public async Task OwnerOfAnotherSessionIsRefusedInThisOne()
    {
        await using var rig = await Rig.CreateAsync();
        // bob owns a session of his own; that does not make him anyone in alice's thread.
        await rig.Db.AddSessionAsync("bob", "b1");
        await rig.TextAsync("U_BOB", Secret);
        Assert.Empty(rig.Pods.Confirmed);
    }

    [PostgreSqlFact]
    public async Task RefusalNoticeIsPerSession()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.Db.AddSessionAsync("alice", "s2");
        await rig.Threads.UpsertAsync(new SlackThread("s2", "alice", Channel, "1700000000.000200"));
        await rig.TextAsync("U_MALLORY", "x");
        await rig.TextAsync("U_MALLORY", "x", threadTs: "1700000000.000200");
        Assert.Equal(2, rig.Slack.Ephemerals.Count);
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required PostgresSharingDatabase Db { get; init; }
        public required UserDirectory Users { get; init; }
        public required SlackThreadStore Threads { get; init; }
        public required PermissionStore Permissions { get; init; }
        public required StandInSlack Slack { get; init; }
        public required RecordingPods Pods { get; init; }
        public required SlackSocketModeService Service { get; init; }

        public static async Task<Rig> CreateAsync(bool licensed = true)
        {
            var db = await PostgresSharingDatabase.CreateAsync(pooling: false);
            await db.AddSessionAsync("alice", "s1");
            foreach (var owner in new[] { "alice", "bob", "vic", "mallory" })
                await db.AddUserAsync(owner);
            var cfg = db.Configuration;
            var users = new UserDirectory(cfg);
            // The binding store owns chat_deleted_sessions, which the Slack thread store reads.
            await new ChatBindingStore(cfg).InitializeAsync();
            var threads = new SlackThreadStore(cfg);
            await threads.InitializeAsync();
            await threads.UpsertAsync(new SlackThread("s1", "alice", Channel, ThreadTs));
            var permissions = new PermissionStore(cfg);
            await permissions.InitializeAsync();
            await permissions.CreateAsync(new PermissionRequest { Id = "req1", SessionId = "s1", Owner = "alice", Tool = "Bash" });
            await permissions.SetPromptMessageAsync("req1", "slack", Channel, "1700000000.000300");

            var slack = new StandInSlack(new Dictionary<string, string?>
            {
                ["U_ALICE"] = "alice@example.test", ["U_BOB"] = "BOB@example.test", ["U_VIC"] = "vic@example.test",
                ["U_MALLORY"] = "mallory@example.test", ["U_STRANGER"] = "nobody@example.com", ["U_NOEMAIL"] = null
            });
            var opts = new SlackOptions { Enabled = true, BotToken = "xoxb-test", AppToken = "xapp-test" };
            var client = new SlackClient(new Factory(slack), opts, NullLogger<SlackClient>.Instance);
            var license = new FakeEnterpriseLicense(licensed);
            var gate = new SlackSenderGate(client, users, new SessionAccessService(db.Shares, license));
            var sessions = DispatchProxy.Create<ISessionService, RunningSessions>();
            ((RunningSessions)(object)sessions).Store = db.Sessions;
            var pods = new RecordingPods();
            var service = new SlackSocketModeService(opts, license, client, threads, permissions, sessions, users,
                new ChatRepoService(null!, NullLogger<ChatRepoService>.Instance), new WorkingIndicator(),
                pods, gate, new ConfigurationBuilder().Build(), NullLogger<SlackSocketModeService>.Instance);
            return new Rig
            {
                Db = db, Users = users, Threads = threads, Permissions = permissions,
                Slack = slack, Pods = pods, Service = service
            };
        }

        public Task TextAsync(string user, string text, string channel = Channel, string threadTs = ThreadTs)
            => SendAsync(new
            {
                type = "events_api", envelope_id = "e1",
                payload = new { @event = new { type = "message", user, text, channel, thread_ts = threadTs, ts = "1700000001.000000" } }
            });

        public Task ClickAsync(string? user, string decision)
            => SendAsync(new
            {
                type = "interactive", envelope_id = "e2",
                payload = new
                {
                    type = "block_actions",
                    user = user is null ? null : new { id = user, username = user.ToLowerInvariant() },
                    container = new { type = "message", channel_id = Channel, message_ts = "1700000000.000300", thread_ts = ThreadTs },
                    actions = new[] { new { action_id = PermissionAction.Id(decision, "req1"), value = "req1" } }
                }
            });

        public Task<string?> DecisionAsync() => Permissions.GetDecisionAsync("req1");

        private async Task SendAsync(object envelope)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
            await Service.HandleEnvelopeAsync(doc.RootElement, CancellationToken.None);
        }

        public async ValueTask DisposeAsync() => await Db.DisposeAsync();
    }

    public sealed record SlackCall(string Method, string Body)
    {
        private JsonElement Json => JsonDocument.Parse(Body).RootElement;
        public string? Thread => Json.TryGetProperty("thread_ts", out var t) ? t.GetString() : null;
        public string? User => Json.TryGetProperty("user", out var u) ? u.GetString() : null;
        public string? Text => Json.TryGetProperty("text", out var t) ? t.GetString() : null;
    }

    /// <summary>Answers the Slack Web API calls the service makes and records every write.</summary>
    internal sealed class StandInSlack(IReadOnlyDictionary<string, string?> emails) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<SlackCall> _posts = new();
        private int _ts;
        public IReadOnlyList<SlackCall> Posts => _posts.ToList();
        public IReadOnlyList<SlackCall> Ephemerals => Posts.Where(p => p.Method == "chat.postEphemeral").ToList();
        public void Clear() => _posts.Clear();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.AbsolutePath.TrimStart('/').Replace("api/", "");
            if (method == "users.info")
            {
                var id = Uri.UnescapeDataString(request.RequestUri.Query.Split("user=")[1]);
                object user = !emails.TryGetValue(id, out var email)
                    ? new { ok = false, error = "user_not_found" }
                    : new { ok = true, user = new { id, profile = email is null ? (object)new { } : new { email } } };
                return Json(user);
            }
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            _posts.Enqueue(new SlackCall(method, body));
            return Json(new { ok = true, ts = $"1700000099.{Interlocked.Increment(ref _ts):D6}" });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Records which session the reply was about to be typed into. Answering
    /// "not confirmed" stops before the terminal WebSocket; reaching this point at all is
    /// what the gate decides.</summary>
    internal sealed class RecordingPods : IAgentPodRequests
    {
        private readonly ConcurrentQueue<string> _confirmed = new();
        public IReadOnlyList<string> Confirmed => _confirmed.ToList();

        public Task<HttpResponseMessage> SendAsync(string sessionId, HttpRequestMessage request,
            HttpCompletionOption completion, TimeSpan timeout, CancellationToken ct)
            => throw new InvalidOperationException("not used");

        public Task ConfirmAsync(string sessionId, string? podIp, CancellationToken ct)
        {
            _confirmed.Enqueue(sessionId);
            throw new AgentPodNotConfirmedException("stand-in pod");
        }
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
                ? new SessionInfo { Id = r.Id, Owner = r.Owner, Title = r.Title, Mode = r.Mode, Phase = "Running", PodIp = "10.0.0.9" }
                : null;
    }
}
