using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Project agent fleet: directory + messaging endpoints on <see cref="InternalController"/>
/// and the remote/public counterparts. Authorization is the point of most of these tests:
/// peers only within the same owner AND the same project; anything else answers 404.
/// </summary>
public class InternalAgentFleetTests
{
    private const string Owner = "alice";
    private const string ProjectA = "proj-a";
    private const string ProjectB = "proj-b";

    // ------------------------------------------------------------------ directory

    [Fact]
    public async Task ProjectAgents_ListsSameOwnerSameProject_WithSelfMarker()
    {
        var world = World();
        var controller = world.Controller("reviewer");

        var result = await controller.ProjectAgents("reviewer", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<IReadOnlyList<ProjectAgentInfo>>(ok.Value);
        Assert.Equal(["coder", "reviewer", "watcher"], agents.Select(a => a.Id).OrderBy(x => x));
        Assert.True(Assert.Single(agents, a => a.Id == "reviewer").Self);
        Assert.Equal("Implements tasks", Assert.Single(agents, a => a.Id == "coder").Description);
        // Foreign projects and foreign owners never show up.
        Assert.DoesNotContain(agents, a => a.Id is "other-project" or "mallory-session");
    }

    [Fact]
    public async Task ProjectAgents_WithoutProject_SeesOnlySelfAndDescendants()
    {
        var world = World();
        var controller = world.Controller("rootless");

        var result = await controller.ProjectAgents("rootless", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var agents = Assert.IsAssignableFrom<IReadOnlyList<ProjectAgentInfo>>(ok.Value);
        Assert.Equal(["rootless", "rootless-child"], agents.Select(a => a.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task ProjectAgents_WrongToken_ReturnsUnauthorized()
    {
        var world = World();
        var controller = world.Controller("reviewer", token: "wrong");

        Assert.IsType<UnauthorizedResult>(await controller.ProjectAgents("reviewer", CancellationToken.None));
    }

    // ------------------------------------------------------------------ send

    [Fact]
    public async Task Send_ToPeerInSameProject_StoresMessageAndNotifies()
    {
        var world = World();
        var controller = world.Controller("reviewer");

        var result = await controller.SendMessage("reviewer",
            new SendAgentMessageRequest("coder", "Please fix MR 42"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var stored = Assert.Single(world.Messages.Added);
        Assert.Equal("reviewer", stored.FromSessionId);
        Assert.Equal("coder", stored.ToSessionId);
        Assert.Equal(ProjectA, stored.ProjectId);
        Assert.Equal(Owner, stored.Owner);
        Assert.Equal("Please fix MR 42", stored.Body);
        Assert.Null(stored.DeliveredAt);

        var (target, ev, text) = Assert.Single(world.Notifier.Events);
        Assert.Equal("coder", target.Id);
        Assert.Equal("agent-message", ev);
        Assert.Contains("Please fix MR 42", text);
    }

    [Fact]
    public async Task Send_ToSessionInAnotherProject_Returns404WithoutLeaking()
    {
        var world = World();
        var controller = world.Controller("reviewer");

        var result = await controller.SendMessage("reviewer",
            new SendAgentMessageRequest("other-project", "hi"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(world.Messages.Added);
    }

    [Fact]
    public async Task Send_ToForeignOwnersSession_Returns404()
    {
        var world = World();
        var controller = world.Controller("reviewer");

        // mallory-session lives in the same project id but belongs to another owner.
        var result = await controller.SendMessage("reviewer",
            new SendAgentMessageRequest("mallory-session", "hi"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(world.Messages.Added);
    }

    [Fact]
    public async Task Send_FromSessionWithoutProject_Returns404()
    {
        var world = World();
        var controller = world.Controller("rootless");

        var result = await controller.SendMessage("rootless",
            new SendAgentMessageRequest("coder", "hi"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(world.Messages.Added);
    }

    [Theory]
    [InlineData("rootless", "rootless-child")]
    [InlineData("rootless-child", "rootless")]
    [InlineData("coder", "legacy-child")]
    [InlineData("legacy-child", "coder")]
    public async Task Send_AlongTheParentChildLine_IsDeliveredWithoutASharedProject(string from, string to)
    {
        var world = World();
        var controller = world.Controller(from);

        var result = await controller.SendMessage(from,
            new SendAgentMessageRequest(to, "status?"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var stored = Assert.Single(world.Messages.Added);
        Assert.Equal(from, stored.FromSessionId);
        Assert.Equal(to, stored.ToSessionId);
    }

    [Fact]
    public async Task Send_FromProjectlessChildToItsParentsProjectPeer_Returns404()
    {
        var world = World();
        var controller = world.Controller("legacy-child");

        var result = await controller.SendMessage("legacy-child",
            new SendAgentMessageRequest("reviewer", "hi"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(world.Messages.Added);
    }

    [Fact]
    public async Task ProjectAgents_IncludesOwnChildrenOutsideTheProject()
    {
        var world = World();
        var controller = world.Controller("coder");

        var ok = Assert.IsType<OkObjectResult>(await controller.ProjectAgents("coder", CancellationToken.None));
        var ids = Assert.IsAssignableFrom<IEnumerable<ProjectAgentInfo>>(ok.Value).Select(a => a.Id).ToList();

        Assert.Contains("legacy-child", ids);
        Assert.Contains("reviewer", ids);
        Assert.DoesNotContain("rootless-child", ids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Send_WithEmptyBody_ReturnsBadRequest(string? body)
    {
        var world = World();
        var controller = world.Controller("reviewer");

        var result = await controller.SendMessage("reviewer",
            new SendAgentMessageRequest("coder", body), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(world.Messages.Added);
    }

    [Fact]
    public async Task Send_WithOversizedBody_ReturnsBadRequest()
    {
        var world = World();
        var controller = world.Controller("reviewer");

        var result = await controller.SendMessage("reviewer",
            new SendAgentMessageRequest("coder", new string('x', AgentMessaging.MaxBodyChars + 1)),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(world.Messages.Added);
    }

    [Fact]
    public async Task Send_ToItself_ReturnsBadRequest()
    {
        var world = World();
        var controller = world.Controller("reviewer");

        var result = await controller.SendMessage("reviewer",
            new SendAgentMessageRequest("reviewer", "note to self"), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(world.Messages.Added);
    }

    // ------------------------------------------------------------------ inbox

    [Fact]
    public async Task Inbox_TakesUndelivered_AndResolvesSenderTitles()
    {
        var world = World();
        world.Messages.Pending.Add(new SessionMessageRecord
        {
            Id = "m-1", ProjectId = ProjectA, FromSessionId = "reviewer",
            ToSessionId = "coder", Owner = Owner, Body = "Please fix MR 42"
        });
        world.Messages.Pending.Add(new SessionMessageRecord
        {
            Id = "m-2", ProjectId = ProjectA, FromSessionId = null,
            ToSessionId = "coder", Owner = Owner, Body = "external task"
        });
        var controller = world.Controller("coder");

        var result = await controller.InboxMessages("coder", CancellationToken.None, wait: 0);

        var ok = Assert.IsType<OkObjectResult>(result);
        var messages = MessagesOf(ok.Value!);
        Assert.Equal(2, messages.Count);
        Assert.Equal("Code Reviewer", messages[0].FromTitle);
        Assert.Equal("Please fix MR 42", messages[0].Body);
        Assert.Null(messages[1].From);
        Assert.Null(messages[1].FromTitle);
        // Taking marked them delivered — the next poll is empty.
        var again = Assert.IsType<OkObjectResult>(
            await controller.InboxMessages("coder", CancellationToken.None, wait: 0));
        Assert.Empty(MessagesOf(again.Value!));
    }

    [Fact]
    public async Task Inbox_LongPoll_TimesOutEmpty()
    {
        var world = World();
        var controller = world.Controller("coder");

        var started = DateTime.UtcNow;
        var result = await controller.InboxMessages("coder", CancellationToken.None, wait: 1);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Empty(MessagesOf(ok.Value!));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(1));
        Assert.True(world.Messages.TakeCalls >= 2); // it actually polled
    }

    [Fact]
    public async Task Inbox_WrongToken_ReturnsUnauthorized()
    {
        var world = World();
        var controller = world.Controller("coder", token: "wrong");

        Assert.IsType<UnauthorizedResult>(
            await controller.InboxMessages("coder", CancellationToken.None, wait: 0));
    }

    // ------------------------------------------------------------------ remote send

    [Fact]
    public async Task RemoteSend_StoresExternalMessageForOwnedSession()
    {
        var world = World();
        var controller = RemoteControllerFor(world, token: "oah_valid");

        var result = await controller.SendMessage("coder",
            new RemoteAgentMessageRequest("Task from the outside"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var stored = Assert.Single(world.Messages.Added);
        Assert.Null(stored.FromSessionId);
        Assert.Equal("coder", stored.ToSessionId);
        Assert.Equal(ProjectA, stored.ProjectId);
        Assert.Equal(Owner, stored.Owner);
    }

    [Fact]
    public async Task RemoteSend_ForeignSessionOrBadToken_Fails()
    {
        var world = World();

        var badToken = RemoteControllerFor(world, token: "oah_wrong");
        Assert.IsType<UnauthorizedResult>(await badToken.SendMessage("coder",
            new RemoteAgentMessageRequest("x"), CancellationToken.None));

        var controller = RemoteControllerFor(world, token: "oah_valid");
        Assert.IsType<NotFoundResult>(await controller.SendMessage("mallory-session",
            new RemoteAgentMessageRequest("x"), CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await controller.SendMessage("coder",
            new RemoteAgentMessageRequest(new string('x', AgentMessaging.MaxBodyChars + 1)), CancellationToken.None));
        Assert.Empty(world.Messages.Added);
    }

    // ------------------------------------------------------------------ public message view

    [Fact]
    public async Task PublicMessages_ListsRecentWithoutMarkingDelivered()
    {
        var world = World();
        world.Messages.Recent.Add(new SessionMessageRecord
        {
            Id = "m-1", ProjectId = ProjectA, FromSessionId = "reviewer",
            ToSessionId = "coder", Owner = Owner, Body = "Please fix MR 42"
        });
        var controller = SessionsControllerFor(world, user: Owner);

        var result = await controller.Messages("coder", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var messages = Assert.IsAssignableFrom<IEnumerable<AgentMessageInfo>>(ok.Value).ToList();
        var message = Assert.Single(messages);
        Assert.Equal("Code Reviewer", message.FromTitle);
        Assert.Null(message.DeliveredAt);
        Assert.Equal(0, world.Messages.TakeCalls); // the UI view never consumes the inbox
    }

    [Fact]
    public async Task PublicMessages_ForeignSession_Returns404()
    {
        var world = World();
        var controller = SessionsControllerFor(world, user: "mallory");

        Assert.IsType<NotFoundResult>(await controller.Messages("coder", CancellationToken.None));
    }

    // ------------------------------------------------------------------ duplication carries the description

    [Fact]
    public void Duplicate_CopiesTheDescription()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = Owner, Title = "Reviewer",
            Description = "Reviews merge requests.",
            Mode = SessionMode.Interactive, AgentSessionId = "a", CallbackToken = "t"
        };

        var copy = SessionDuplication.CopyableRequest(source, new DuplicateSessionRequest("Copy", null, IncludeMcp: false));

        Assert.Equal("Reviews merge requests.", copy.Description);
    }

    [Fact]
    public void DescriptionNormalize_TrimsClearsAndCaps()
    {
        Assert.Null(SessionDescription.Normalize(null));
        Assert.Null(SessionDescription.Normalize("   "));
        Assert.Equal("watches issues", SessionDescription.Normalize("  watches issues  "));
        Assert.Throws<ArgumentException>(() =>
            SessionDescription.Normalize(new string('x', SessionDescription.MaxLength + 1)));
    }

    // ------------------------------------------------------------------ fixtures

    private static FleetWorld World()
    {
        var sessions = new List<SessionRecord>
        {
            Record("reviewer", Owner, ProjectA, title: "Code Reviewer", description: "Reviews merge requests"),
            Record("watcher", Owner, ProjectA, title: "Issue Watcher", description: "Watches the issue tracker"),
            Record("coder", Owner, ProjectA, title: "Coder", description: "Implements tasks"),
            Record("other-project", Owner, ProjectB, title: "Elsewhere"),
            Record("rootless", Owner, project: null, title: "Rootless"),
            Record("rootless-child", Owner, project: null, title: "Rootless child", parent: "rootless"),
            Record("legacy-child", Owner, project: null, title: "Legacy child", parent: "coder"),
            Record("mallory-session", "mallory", ProjectA, title: "Mallory")
        };
        return new FleetWorld(sessions);
    }

    private static SessionRecord Record(string id, string owner, string? project,
        string title, string? description = null, string? parent = null) => new()
    {
        Id = id,
        Owner = owner,
        Title = title,
        Description = description,
        ProjectId = project,
        ParentSessionId = parent,
        Mode = SessionMode.Autonomous,
        AgentSessionId = $"agent-{id}",
        CallbackToken = $"token-{id}"
    };

    private static RemoteController RemoteControllerFor(FleetWorld world, string token)
    {
        var controller = new RemoteController(
            (t, _) => Task.FromResult(t == "oah_valid" ? new RemoteCaller(Owner, null) : null),
            world.Service, world.Messages)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers.Authorization = $"Bearer {token}";
        return controller;
    }

    private static SessionsController SessionsControllerFor(FleetWorld world, string user)
    {
        var controller = new SessionsController(world.Service, null!, [], world.Messages)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("preferred_username", user)], "test"));
        return controller;
    }

    private static List<AgentMessageInfo> MessagesOf(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("messages")
            .Deserialize<List<AgentMessageInfo>>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed class FleetWorld
    {
        public FleetWorld(List<SessionRecord> records)
        {
            Records = records;
            Store = new ListSessionStore(records);
            Service = new ListSessionService(records);
        }

        public List<SessionRecord> Records { get; }
        public ListSessionStore Store { get; }
        public ListSessionService Service { get; }
        public InMemoryMessageStore Messages { get; } = new();
        public RecordingNotifier Notifier { get; } = new();

        public InternalController Controller(string sessionId, string? token = null)
        {
            var controller = new InternalController(Store, [Notifier], Service, null!, [], [], null!, null!,
                browsers: null, spawnMcpEnabled: true, messages: Messages)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.Request.Headers["X-Agent-Token"] = token ?? $"token-{sessionId}";
            return controller;
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<(SessionRecord Session, string Event, string Message)> Events { get; } = [];

        public Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
        {
            Events.Add((s, eventType, message));
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryMessageStore : ISessionMessageStore
    {
        public List<SessionMessageRecord> Added { get; } = [];
        public List<SessionMessageRecord> Pending { get; } = [];
        public List<SessionMessageRecord> Recent { get; } = [];
        public int TakeCalls { get; private set; }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task AddAsync(SessionMessageRecord message, CancellationToken ct = default)
        {
            Added.Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionMessageRecord>> TakeUndeliveredAsync(string toSessionId, int limit, CancellationToken ct = default)
        {
            TakeCalls++;
            var taken = Pending.Where(m => m.ToSessionId == toSessionId && m.DeliveredAt is null)
                .OrderBy(m => m.CreatedAt).Take(limit).ToList();
            foreach (var m in taken) m.DeliveredAt = DateTime.UtcNow;
            return Task.FromResult<IReadOnlyList<SessionMessageRecord>>(taken);
        }

        public Task<IReadOnlyList<SessionMessageRecord>> ListRecentAsync(string toSessionId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionMessageRecord>>(Recent
                .Where(m => m.ToSessionId == toSessionId)
                .OrderByDescending(m => m.CreatedAt).Take(limit).ToList());

        public Task MarkDeliveredAsync(string id, string via, CancellationToken ct = default)
        {
            foreach (var m in Added.Concat(Pending).Where(m => m.Id == id && m.DeliveredAt is null))
            {
                m.DeliveredAt = DateTime.UtcNow;
                m.DeliveredVia = via;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class ListSessionStore(List<SessionRecord> records) : ISessionStore
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

    private sealed class ListSessionService(List<SessionRecord> records) : ISessionService
    {
        private static SessionInfo Info(SessionRecord r) => new()
        {
            Id = r.Id, Title = r.Title, Description = r.Description, Owner = r.Owner,
            ProjectId = r.ProjectId, ParentSessionId = r.ParentSessionId,
            Mode = r.Mode, Phase = "Running"
        };

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>(records.Where(r => r.Owner == owner).Select(Info).ToList());

        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(records.Where(r => r.Owner == owner && r.Id == id).Select(Info).FirstOrDefault());

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
