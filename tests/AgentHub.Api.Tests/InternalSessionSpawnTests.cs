using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public class InternalSessionSpawnTests
{
    private const string ParentId = "parent-1";
    private const string ParentToken = "parent-callback-token";
    private const string Owner = "alice";

    [Fact]
    public async Task Spawn_WithMatchingToken_CreatesUnderParent()
    {
        var svc = new RecordingSessionService();
        var controller = Controller(Parent(), svc);

        var result = await controller.Spawn(ParentId, new CreateSessionRequest
        {
            Title = "child",
            Mode = SessionMode.Autonomous,
            Prompt = "do work",
            ParentSessionId = "escape-attempt"
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var info = Assert.IsType<SessionInfo>(ok.Value);
        Assert.Equal("new-child", info.Id);
        Assert.Equal(1, svc.CreateCalls);
        Assert.Equal(Owner, svc.CreateOwner);
        Assert.NotNull(svc.CreateRequest);
        Assert.Equal(ParentId, svc.CreateRequest!.ParentSessionId);
        Assert.Equal("child", svc.CreateRequest.Title);
    }

    [Fact]
    public async Task Spawn_WithWrongToken_ReturnsUnauthorized()
    {
        var svc = new RecordingSessionService();
        var controller = Controller(Parent(), svc, token: "wrong-token");

        var result = await controller.Spawn(ParentId,
            new CreateSessionRequest { Title = "x", Mode = SessionMode.Autonomous, Prompt = "p" },
            CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(0, svc.CreateCalls);
    }

    [Fact]
    public async Task Spawn_WithTokenForDifferentSession_ReturnsUnauthorized()
    {
        // Token authenticates a different session than the path id.
        var other = new SessionRecord
        {
            Id = "other-session",
            Owner = Owner,
            CallbackToken = ParentToken,
            Mode = SessionMode.Autonomous,
            Title = "other"
        };
        var svc = new RecordingSessionService();
        var controller = Controller(other, svc);

        var result = await controller.Spawn(ParentId,
            new CreateSessionRequest { Title = "x", Mode = SessionMode.Autonomous, Prompt = "p" },
            CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(0, svc.CreateCalls);
    }

    [Fact]
    public async Task Spawn_WhenSoftLimitExceeded_Returns429()
    {
        var svc = new RecordingSessionService
        {
            CreateException = new SessionLimitExceededException("Running session limit reached (20 of 20).")
        };
        var controller = Controller(Parent(), svc);

        var result = await controller.Spawn(ParentId,
            new CreateSessionRequest { Title = "x", Mode = SessionMode.Autonomous, Prompt = "p" },
            CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(429, status.StatusCode);
        Assert.Equal(svc.CreateException.Message, status.Value);
    }

    [Fact]
    public async Task Spawn_WhenCreateValidationFails_ReturnsBadRequest()
    {
        var svc = new RecordingSessionService
        {
            CreateException = new ArgumentException("A prompt is required for Autonomous/Scheduled sessions.")
        };
        var controller = Controller(Parent(), svc);

        var result = await controller.Spawn(ParentId,
            new CreateSessionRequest { Title = "x", Mode = SessionMode.Autonomous },
            CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(svc.CreateException.Message, bad.Value);
        Assert.Equal(0, svc.CreateCalls);
    }

    [Fact]
    public async Task Spawn_WhenSpawnMcpDisabled_ReturnsNotFound()
    {
        var svc = new RecordingSessionService();
        var controller = Controller(Parent(), svc, spawnMcpEnabled: false);

        var result = await controller.Spawn(ParentId,
            new CreateSessionRequest { Title = "x", Mode = SessionMode.Autonomous, Prompt = "p" },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, svc.CreateCalls);
    }

    [Fact]
    public async Task Children_ListsDirectChildrenOnly()
    {
        var svc = new RecordingSessionService
        {
            Sessions =
            [
                Info("child-a", ParentId),
                Info("child-b", ParentId),
                Info("grandchild", "child-a"),
                Info("sibling-root", null)
            ]
        };
        var controller = Controller(Parent(), svc);

        var result = await controller.Children(ParentId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<SessionInfo>>(ok.Value);
        Assert.Equal(2, list.Count);
        Assert.All(list, s => Assert.Equal(ParentId, s.ParentSessionId));
    }

    [Fact]
    public async Task GetPeer_Descendant_ReturnsSession()
    {
        var grandchild = Info("grandchild", "child-a");
        var svc = new RecordingSessionService
        {
            Sessions =
            [
                Info("child-a", ParentId),
                grandchild,
                Info("sibling", ParentId)
            ]
        };
        var controller = Controller(Parent(), svc);

        var result = await controller.GetPeer(ParentId, "grandchild", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var info = Assert.IsType<SessionInfo>(ok.Value);
        Assert.Equal("grandchild", info.Id);
    }

    [Fact]
    public async Task GetPeer_Sibling_ReturnsNotFound()
    {
        var childA = new SessionRecord
        {
            Id = "child-a",
            Owner = Owner,
            CallbackToken = "child-a-token",
            Mode = SessionMode.Autonomous,
            ParentSessionId = ParentId,
            Title = "child-a"
        };
        var svc = new RecordingSessionService
        {
            Sessions =
            [
                Info(ParentId, null),
                Info("child-a", ParentId),
                Info("child-b", ParentId)
            ]
        };
        var controller = Controller(childA, svc, token: "child-a-token");

        var result = await controller.GetPeer("child-a", "child-b", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeletePeer_Descendant_Deletes()
    {
        var svc = new RecordingSessionService
        {
            Sessions =
            [
                Info("child-a", ParentId),
                Info("grandchild", "child-a")
            ]
        };
        var controller = Controller(Parent(), svc);

        var result = await controller.DeletePeer(ParentId, "grandchild", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, svc.DeleteCalls);
        Assert.Equal(Owner, svc.DeleteOwner);
        Assert.Equal("grandchild", svc.DeleteId);
    }

    [Fact]
    public async Task DeletePeer_Sibling_ReturnsNotFound()
    {
        var childA = new SessionRecord
        {
            Id = "child-a",
            Owner = Owner,
            CallbackToken = "child-a-token",
            Mode = SessionMode.Autonomous,
            ParentSessionId = ParentId,
            Title = "child-a"
        };
        var svc = new RecordingSessionService
        {
            Sessions =
            [
                Info(ParentId, null),
                Info("child-a", ParentId),
                Info("child-b", ParentId)
            ]
        };
        var controller = Controller(childA, svc, token: "child-a-token");

        var result = await controller.DeletePeer("child-a", "child-b", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal(0, svc.DeleteCalls);
    }

    [Fact]
    public async Task Children_WrongToken_ReturnsUnauthorized()
    {
        var controller = Controller(Parent(), new RecordingSessionService(), token: "bad");

        var result = await controller.Children(ParentId, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
    }

    // ------------------------------------------------------------------ fixtures

    private static SessionRecord Parent() => new()
    {
        Id = ParentId,
        Owner = Owner,
        CallbackToken = ParentToken,
        Mode = SessionMode.Autonomous,
        Title = "parent"
    };

    private static SessionInfo Info(string id, string? parentId) => new()
    {
        Id = id,
        Title = id,
        Owner = Owner,
        Mode = SessionMode.Autonomous,
        Phase = "Running",
        ParentSessionId = parentId
    };

    private static InternalController Controller(
        SessionRecord session,
        RecordingSessionService svc,
        string token = ParentToken,
        bool spawnMcpEnabled = true)
    {
        var controller = new InternalController(
            new CallbackSessionStore(session), [], svc, null!, [], [], null!, null!,
            browsers: null, spawnMcpEnabled: spawnMcpEnabled)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Agent-Token"] = token;
        return controller;
    }

    private sealed class CallbackSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(owner == session.Owner && id == session.Id ? session : null);
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(id == session.Id ? session : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingSessionService : ISessionService
    {
        public int CreateCalls { get; private set; }
        public string? CreateOwner { get; private set; }
        public CreateSessionRequest? CreateRequest { get; private set; }
        public Exception? CreateException { get; init; }

        public int DeleteCalls { get; private set; }
        public string? DeleteOwner { get; private set; }
        public string? DeleteId { get; private set; }

        public List<SessionInfo> Sessions { get; init; } = [];

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
        {
            if (CreateException is not null) throw CreateException;
            CreateCalls++;
            CreateOwner = owner;
            CreateRequest = req;
            return Task.FromResult(new SessionInfo
            {
                Id = "new-child",
                Title = req.Title,
                Owner = owner,
                Mode = req.Mode,
                Phase = "Pending",
                ParentSessionId = req.ParentSessionId
            });
        }

        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionInfo>>(Sessions.Where(s => s.Owner == owner).ToList());

        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(Sessions.FirstOrDefault(s => s.Owner == owner && s.Id == id));

        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default)
        {
            DeleteCalls++;
            DeleteOwner = owner;
            DeleteId = id;
            return Task.CompletedTask;
        }
    }
}
