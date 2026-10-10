using System.Net;
using System.Security.Claims;
using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Mcp;
using AgentHub.Api.Models;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Priority messages (docs/priority-messages.md): the push into a running pod, what the sender
/// is told, and that every send surface stores the flags and asks for the push. The pod itself
/// is a fake HTTP handler here; the session agent's side is covered by the node tests.
/// </summary>
public class PriorityMessageTests
{
    private const string Owner = "alice";

    // ------------------------------------------------------------------ flags

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData(true, null, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(null, true, true, true)]
    [InlineData(true, false, true, false)]
    public void Interrupt_ImpliesPriority(bool? priority, bool? interrupt, bool expectPriority, bool expectInterrupt)
    {
        var (p, i) = AgentMessaging.ResolveFlags(priority, interrupt);
        Assert.Equal(expectPriority, p);
        Assert.Equal(expectInterrupt, i);
    }

    // ------------------------------------------------------------------ delivery service

    [Fact]
    public async Task Delivery_RunningPodTakesIt_MarksInjected()
    {
        var pod = new FakePod(HttpStatusCode.OK, """{"delivered":"pty"}""");
        var world = new DeliveryWorld(pod);

        var result = await world.Delivery.TryInjectAsync(Info("coder", "Running", "10.0.0.5"),
            Message("m-1", priority: true, interrupt: true), "Code Reviewer", CancellationToken.None);

        Assert.Equal(MessageDeliveryVia.Injected, result.Via);
        Assert.Null(result.Reason);
        Assert.Equal("http://10.0.0.5:7681/agenthub/messages", pod.Request!.RequestUri!.ToString());
        Assert.Equal("token-coder", pod.Request.Headers.GetValues("X-Agent-Token").Single());
        using var body = JsonDocument.Parse(pod.Body!);
        Assert.Equal("m-1", body.RootElement.GetProperty("id").GetString());
        Assert.Equal("reviewer", body.RootElement.GetProperty("from").GetString());
        Assert.Equal("Code Reviewer", body.RootElement.GetProperty("fromTitle").GetString());
        Assert.True(body.RootElement.GetProperty("priority").GetBoolean());
        Assert.True(body.RootElement.GetProperty("interrupt").GetBoolean());
        Assert.Equal([("m-1", MessageDeliveryVia.Injected)], world.Messages.Marked);
    }

    [Fact]
    public async Task Delivery_ChatModeAndModQueue_ReportTheirOwnChannel()
    {
        var chat = new DeliveryWorld(new FakePod(HttpStatusCode.OK, """{"delivered":"chat"}"""));
        var mod = new DeliveryWorld(new FakePod(HttpStatusCode.OK, """{"delivered":"queued-for-mod"}"""));

        var viaChat = await chat.Delivery.TryInjectAsync(Info("coder", "Running", "10.0.0.5"), Message("m-1"), null, CancellationToken.None);
        var viaMod = await mod.Delivery.TryInjectAsync(Info("coder", "Running", "10.0.0.5"), Message("m-2"), null, CancellationToken.None);

        Assert.Equal(MessageDeliveryVia.Injected, viaChat.Via);
        Assert.Equal(MessageDeliveryVia.Mod, viaMod.Via);
        Assert.Equal([("m-2", MessageDeliveryVia.Mod)], mod.Messages.Marked);
    }

    [Fact]
    public async Task Delivery_PendingSession_StaysInInboxWithoutTouchingThePod()
    {
        var pod = new FakePod(HttpStatusCode.OK, """{"delivered":"pty"}""");
        var world = new DeliveryWorld(pod);

        var pending = await world.Delivery.TryInjectAsync(Info("coder", "Pending", null), Message("m-1"), null, CancellationToken.None);
        var noIp = await world.Delivery.TryInjectAsync(Info("coder", "Running", null), Message("m-1"), null, CancellationToken.None);

        Assert.Equal(MessageDeliveryVia.Inbox, pending.Via);
        Assert.Equal("session_not_running", pending.Reason);
        Assert.Equal(MessageDeliveryVia.Inbox, noIp.Via);
        Assert.Null(pod.Request);
        Assert.Empty(world.Messages.Marked);
    }

    [Fact]
    public async Task Delivery_PodSaysUnavailable_LeavesTheMessageInTheInbox()
    {
        var world = new DeliveryWorld(new FakePod(HttpStatusCode.OK, """{"delivered":"unavailable","reason":"non_interactive"}"""));

        var result = await world.Delivery.TryInjectAsync(Info("coder", "Running", "10.0.0.5"), Message("m-1"), null, CancellationToken.None);

        Assert.Equal(MessageDeliveryVia.Inbox, result.Via);
        Assert.Equal("non_interactive", result.Reason);
        Assert.Empty(world.Messages.Marked);
    }

    [Fact]
    public async Task Delivery_PodErrorOrUnreachable_LeavesTheMessageInTheInbox()
    {
        var refused = new DeliveryWorld(new FakePod(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}"""));
        var down = new DeliveryWorld(new FakePod(null, null));
        var junk = new DeliveryWorld(new FakePod(HttpStatusCode.OK, "not json"));

        var a = await refused.Delivery.TryInjectAsync(Info("coder", "Running", "10.0.0.5"), Message("m-1"), null, CancellationToken.None);
        var b = await down.Delivery.TryInjectAsync(Info("coder", "Running", "10.0.0.5"), Message("m-1"), null, CancellationToken.None);
        var c = await junk.Delivery.TryInjectAsync(Info("coder", "Running", "10.0.0.5"), Message("m-1"), null, CancellationToken.None);

        Assert.Equal(("inbox", "pod_http_401"), (a.Via, a.Reason));
        Assert.Equal(("inbox", "pod_unreachable"), (b.Via, b.Reason));
        Assert.Equal(("inbox", "pod_unreachable"), (c.Via, c.Reason));
        Assert.Empty(refused.Messages.Marked);
        Assert.Empty(down.Messages.Marked);
        Assert.Empty(junk.Messages.Marked);
    }

    [Fact]
    public async Task Dispatch_WithoutAServiceOrTarget_IsTheInbox()
    {
        var delivery = new RecordingDelivery();

        var none = await AgentMessageDispatch.PushAsync(null, Info("coder", "Running", "1.2.3.4"), Message("m-1"), null, CancellationToken.None);
        var noTarget = await AgentMessageDispatch.PushAsync(delivery, null, Message("m-1"), null, CancellationToken.None);

        Assert.Equal(MessageDeliveryVia.Inbox, none.Via);
        Assert.Equal(MessageDeliveryVia.Inbox, noTarget.Via);
        Assert.Empty(delivery.Calls);
    }

    // ------------------------------------------------------------------ internal send

    [Fact]
    public async Task InternalSend_WithInterrupt_StoresBothFlagsAndPushesWithTheSenderTitle()
    {
        var world = new ControllerWorld();
        var controller = world.Internal("reviewer");

        var result = await controller.SendMessage("reviewer",
            new SendAgentMessageRequest("coder", "Stop and look at MR 42", Priority: null, Interrupt: true), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var sent = Assert.IsType<AgentMessageSendResult>(ok.Value);
        Assert.Equal(MessageDeliveryVia.Injected, sent.DeliveredVia);
        var stored = Assert.Single(world.Messages.Added);
        Assert.True(stored.Priority);
        Assert.True(stored.Interrupt);
        var call = Assert.Single(world.Delivery.Calls);
        Assert.Equal("coder", call.Target.Id);
        Assert.Equal("10.0.0.2", call.Target.PodIp);
        Assert.Equal("Code Reviewer", call.FromTitle);
        Assert.Same(stored, call.Message);
    }

    [Fact]
    public async Task InternalSend_PlainMessage_StillAsksForAPushButStoresNoFlags()
    {
        var world = new ControllerWorld();
        var controller = world.Internal("reviewer");

        var result = await controller.SendMessage("reviewer",
            new SendAgentMessageRequest("coder", "when you have a minute"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var stored = Assert.Single(world.Messages.Added);
        Assert.False(stored.Priority);
        Assert.False(stored.Interrupt);
        // The pod decides what a non-priority message gets (mod queue or nothing), so it is offered too.
        Assert.Single(world.Delivery.Calls);
    }

    [Fact]
    public async Task InternalSend_WhenThePushFails_TellsTheSenderItWaits()
    {
        var world = new ControllerWorld { Delivery = { Answer = new MessageDelivery(MessageDeliveryVia.Inbox, "pod_unreachable") } };
        var controller = world.Internal("reviewer");

        var ok = Assert.IsType<OkObjectResult>(await controller.SendMessage("reviewer",
            new SendAgentMessageRequest("coder", "urgent", Priority: true), CancellationToken.None));

        var sent = Assert.IsType<AgentMessageSendResult>(ok.Value);
        Assert.Equal(MessageDeliveryVia.Inbox, sent.DeliveredVia);
        Assert.Equal("pod_unreachable", sent.Reason);
    }

    // ------------------------------------------------------------------ remote and owner send

    [Fact]
    public async Task RemoteSend_PriorityFlag_IsStoredAndPushed()
    {
        var world = new ControllerWorld();
        var controller = world.Remote("oah_valid");

        var ok = Assert.IsType<OkObjectResult>(await controller.SendMessage("coder",
            new RemoteAgentMessageRequest("ship it", Priority: true), CancellationToken.None));

        var sent = Assert.IsType<AgentMessageSendResult>(ok.Value);
        Assert.Equal(MessageDeliveryVia.Injected, sent.DeliveredVia);
        var stored = Assert.Single(world.Messages.Added);
        Assert.True(stored.Priority);
        Assert.False(stored.Interrupt);
        Assert.Null(stored.FromSessionId);
        var call = Assert.Single(world.Delivery.Calls);
        Assert.Null(call.FromTitle);
    }

    [Fact]
    public async Task OwnerSend_StoresAnExternalMessageForTheOwnersSession()
    {
        var world = new ControllerWorld();
        var controller = world.Sessions(Owner);

        var ok = Assert.IsType<OkObjectResult>(await controller.SendMessage("coder",
            new RemoteAgentMessageRequest("look at this", Interrupt: true), CancellationToken.None));

        var sent = Assert.IsType<AgentMessageSendResult>(ok.Value);
        Assert.Equal("coder", sent.To);
        var stored = Assert.Single(world.Messages.Added);
        Assert.Null(stored.FromSessionId);
        Assert.Equal(Owner, stored.Owner);
        Assert.True(stored.Priority);
        Assert.True(stored.Interrupt);
        Assert.Single(world.Delivery.Calls);
    }

    [Fact]
    public async Task OwnerSend_ForeignSessionOrEmptyBody_FailsWithoutStoring()
    {
        var world = new ControllerWorld();

        Assert.IsType<NotFoundResult>(await world.Sessions("mallory").SendMessage("coder",
            new RemoteAgentMessageRequest("hi"), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await world.Sessions(Owner).SendMessage("coder",
            new RemoteAgentMessageRequest("   "), CancellationToken.None));

        Assert.Empty(world.Messages.Added);
        Assert.Empty(world.Delivery.Calls);
    }

    [Fact]
    public async Task PublicMessages_CarryTheDeliveryChannel()
    {
        var world = new ControllerWorld();
        world.Messages.Recent.Add(new SessionMessageRecord
        {
            Id = "m-1", FromSessionId = null, ToSessionId = "coder", Owner = Owner, Body = "now",
            Priority = true, Interrupt = true, DeliveredAt = DateTime.UtcNow, DeliveredVia = MessageDeliveryVia.Mod
        });

        var ok = Assert.IsType<OkObjectResult>(await world.Sessions(Owner).Messages("coder", CancellationToken.None));

        var info = Assert.Single(Assert.IsAssignableFrom<IEnumerable<AgentMessageInfo>>(ok.Value));
        Assert.True(info.Priority);
        Assert.True(info.Interrupt);
        Assert.Equal(MessageDeliveryVia.Mod, info.DeliveredVia);
    }

    // ------------------------------------------------------------------ remote MCP

    [Theory]
    [InlineData(null, null, false, false)]
    [InlineData("true", null, true, false)]
    [InlineData("no", "yes", true, true)]
    public async Task McpAgentSend_ReadsTheFlagsAsText(string? priority, string? interrupt, bool expectPriority, bool expectInterrupt)
    {
        var world = new ControllerWorld();
        var tools = world.Mcp(Owner);

        var result = await tools.SendToAgent("Coder", "do it", null, priority, interrupt);

        Assert.Equal("coder", result.To);
        Assert.Equal(MessageDeliveryVia.Injected, result.DeliveredVia);
        var stored = Assert.Single(world.Messages.Added);
        Assert.Equal(expectPriority, stored.Priority);
        Assert.Equal(expectInterrupt, stored.Interrupt);
        Assert.Single(world.Delivery.Calls);
    }

    // ------------------------------------------------------------------ postgres

    [PostgreSqlFact]
    public async Task Postgres_FlagsAndChannel_RoundTrip_AndInboxTakeNamesItself()
    {
        await using var database = await PostgresMessageDatabase.CreateAsync();
        await database.Messages.AddAsync(new SessionMessageRecord
        {
            Id = "m-1", ProjectId = "p", FromSessionId = "a", ToSessionId = "target", Owner = Owner,
            Body = "urgent", Priority = true, Interrupt = true
        });
        await database.Messages.AddAsync(new SessionMessageRecord
        {
            Id = "m-2", ProjectId = "p", FromSessionId = "a", ToSessionId = "target", Owner = Owner, Body = "later"
        });

        var recent = await database.Messages.ListRecentAsync("target", 10);
        var urgent = Assert.Single(recent, m => m.Id == "m-1");
        Assert.True(urgent.Priority);
        Assert.True(urgent.Interrupt);
        Assert.Null(urgent.DeliveredVia);
        var later = Assert.Single(recent, m => m.Id == "m-2");
        Assert.False(later.Priority);

        var taken = await database.Messages.TakeUndeliveredAsync("target", 10);
        Assert.All(taken, m => Assert.Equal(MessageDeliveryVia.Inbox, m.DeliveredVia));
    }

    [PostgreSqlFact]
    public async Task Postgres_MarkDelivered_SetsTheChannelOnce_AndHidesItFromTheInbox()
    {
        await using var database = await PostgresMessageDatabase.CreateAsync();
        await database.Messages.AddAsync(new SessionMessageRecord
        {
            Id = "m-1", ToSessionId = "target", Owner = Owner, Body = "urgent", Priority = true
        });

        await database.Messages.MarkDeliveredAsync("m-1", MessageDeliveryVia.Injected);
        // A second delivery claim (the inbox racing the push) does not rewrite the first.
        await database.Messages.MarkDeliveredAsync("m-1", MessageDeliveryVia.Mod);

        var stored = Assert.Single(await database.Messages.ListRecentAsync("target", 10));
        Assert.NotNull(stored.DeliveredAt);
        Assert.Equal(MessageDeliveryVia.Injected, stored.DeliveredVia);
        Assert.Empty(await database.Messages.TakeUndeliveredAsync("target", 10));
    }

    [PostgreSqlFact]
    public async Task Postgres_RowFromBeforeTheColumns_ReadsAsPlainMessage()
    {
        await using var database = await PostgresMessageDatabase.CreateAsync();
        await using (var connection = new Npgsql.NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new Npgsql.NpgsqlCommand(
                "INSERT INTO session_messages (id, to_session_id, owner, body) VALUES ('old', 'target', 'alice', 'legacy')", connection);
            await insert.ExecuteNonQueryAsync();
        }

        var legacy = Assert.Single(await database.Messages.ListRecentAsync("target", 10));
        Assert.False(legacy.Priority);
        Assert.False(legacy.Interrupt);
        Assert.Null(legacy.DeliveredVia);
    }

    // ------------------------------------------------------------------ fixtures

    private static SessionInfo Info(string id, string phase, string? podIp) => new()
    {
        Id = id, Title = id, Owner = Owner, Mode = SessionMode.Interactive, Phase = phase, PodIp = podIp
    };

    private static SessionMessageRecord Message(string id, bool priority = true, bool interrupt = false) => new()
    {
        Id = id, FromSessionId = "reviewer", ToSessionId = "coder", Owner = Owner, Body = "look",
        Priority = priority, Interrupt = interrupt
    };

    /// <summary>One canned answer from a session pod; a null status means the pod is unreachable.</summary>
    private sealed class FakePod(HttpStatusCode? status, string? body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (status is null) throw new HttpRequestException("connection refused");
            return new HttpResponseMessage(status.Value) { Content = new StringContent(body ?? "") };
        }
    }

    private sealed class DeliveryWorld
    {
        public DeliveryWorld(FakePod pod)
        {
            var config = new ConfigurationBuilder().Build();
            var store = new OneSessionStore(new SessionRecord
            {
                Id = "coder", Owner = Owner, Title = "Coder", Mode = SessionMode.Interactive,
                AgentSessionId = "a", CallbackToken = "token-coder"
            });
            Delivery = new SessionMessageDelivery(new HttpClient(pod), store, Messages, config,
                NullLogger<SessionMessageDelivery>.Instance);
        }

        public SessionMessageDelivery Delivery { get; }
        public MarkingMessageStore Messages { get; } = new();
    }

    private sealed class MarkingMessageStore : ISessionMessageStore
    {
        public List<(string Id, string Via)> Marked { get; } = [];
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAsync(SessionMessageRecord message, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionMessageRecord>> TakeUndeliveredAsync(string toSessionId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionMessageRecord>>([]);
        public Task<IReadOnlyList<SessionMessageRecord>> ListRecentAsync(string toSessionId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionMessageRecord>>([]);
        public Task MarkDeliveredAsync(string id, string via, CancellationToken ct = default)
        {
            Marked.Add((id, via));
            return Task.CompletedTask;
        }
    }

    private sealed class OneSessionStore(SessionRecord record) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(record.Owner == owner && record.Id == id ? record : null);
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(record.Id == id ? record : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(record.CallbackToken == token ? record : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([record]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingDelivery : ISessionMessageDelivery
    {
        public List<(SessionInfo Target, SessionMessageRecord Message, string? FromTitle)> Calls { get; } = [];
        public MessageDelivery Answer { get; set; } = new(MessageDeliveryVia.Injected);

        public Task<MessageDelivery> TryInjectAsync(SessionInfo target, SessionMessageRecord message, string? fromTitle, CancellationToken ct)
        {
            Calls.Add((target, message, fromTitle));
            return Task.FromResult(Answer);
        }
    }

    /// <summary>Two peers of one project, both running, so every surface can be exercised.</summary>
    private sealed class ControllerWorld
    {
        private readonly List<SessionRecord> _records =
        [
            Record("reviewer", "Code Reviewer"),
            Record("coder", "Coder")
        ];

        public InMemoryMessages Messages { get; } = new();
        public RecordingDelivery Delivery { get; } = new();

        private static SessionRecord Record(string id, string title) => new()
        {
            Id = id, Owner = Owner, Title = title, ProjectId = "proj", Mode = SessionMode.Interactive,
            AgentSessionId = $"agent-{id}", CallbackToken = $"token-{id}"
        };

        public InternalController Internal(string sessionId)
        {
            var controller = new InternalController(new ListStore(_records), [], new ListService(_records), null!, [], [], null!, null!,
                browsers: null, spawnMcpEnabled: true, messages: Messages, delivery: Delivery)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.Request.Headers["X-Agent-Token"] = $"token-{sessionId}";
            return controller;
        }

        public RemoteController Remote(string token)
        {
            var controller = new RemoteController(
                (t, _) => Task.FromResult(t == "oah_valid" ? new RemoteCaller(Owner, null) : null),
                new ListService(_records), Messages, Delivery)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.Request.Headers.Authorization = $"Bearer {token}";
            return controller;
        }

        public SessionsController Sessions(string user)
        {
            var controller = new SessionsController(new ListService(_records), null!, [], Messages, Delivery)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], "test"));
            return controller;
        }

        public AgentHubMcpTools Mcp(string user)
        {
            var context = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], "test"))
            };
            return new AgentHubMcpTools(new HttpContextAccessor { HttpContext = context }, new ListService(_records),
                NullLogger<AgentHubMcpTools>.Instance, Messages, Delivery);
        }
    }

    private sealed class InMemoryMessages : ISessionMessageStore
    {
        public List<SessionMessageRecord> Added { get; } = [];
        public List<SessionMessageRecord> Recent { get; } = [];
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAsync(SessionMessageRecord message, CancellationToken ct = default)
        {
            Added.Add(message);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SessionMessageRecord>> TakeUndeliveredAsync(string toSessionId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionMessageRecord>>([]);
        public Task<IReadOnlyList<SessionMessageRecord>> ListRecentAsync(string toSessionId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionMessageRecord>>(Recent.Where(m => m.ToSessionId == toSessionId).ToList());
        public Task MarkDeliveredAsync(string id, string via, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ListStore(List<SessionRecord> records) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(records.FirstOrDefault(r => r.Owner == owner && r.Id == id));
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(records.FirstOrDefault(r => r.Id == id));
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(records.FirstOrDefault(r => r.CallbackToken == token));
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>(records.Where(r => r.Owner == owner).ToList());
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ListService(List<SessionRecord> records) : ISessionService
    {
        private static SessionInfo Info(SessionRecord r) => new()
        {
            Id = r.Id, Title = r.Title, Owner = r.Owner, ProjectId = r.ProjectId, Mode = r.Mode,
            Phase = "Running", PodIp = r.Id == "coder" ? "10.0.0.2" : "10.0.0.1"
        };

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>(records.Where(r => r.Owner == owner).Select(Info).ToList());
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(records.Where(r => r.Owner == owner && r.Id == id).Select(Info).FirstOrDefault());

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
