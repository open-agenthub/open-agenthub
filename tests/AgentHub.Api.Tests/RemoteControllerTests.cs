using System.Security.Claims;
using AgentHub.Api.Agents;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public class RemoteControllerTests
{
    private const string ValidToken = "oah_testtoken1234567890abcdef";

    [Fact]
    public async Task Delete_WithValidOwnerToken_CallsDeleteSessionAsync()
    {
        var svc = new RecordingSessionService();
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken);

        var result = await controller.Delete("session-1", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, svc.DeleteCalls);
        Assert.Equal("alice", svc.DeleteOwner);
        Assert.Equal("session-1", svc.DeleteId);
    }

    [Fact]
    public async Task Delete_WithInvalidToken_ReturnsUnauthorized()
    {
        var svc = new RecordingSessionService();
        var controller = Remote((_, _) => Task.FromResult<string?>(null), svc, "oah_unknown");

        var result = await controller.Delete("session-1", CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Equal(0, svc.DeleteCalls);
    }

    [Fact]
    public async Task Create_WhenLimitExceeded_Returns429()
    {
        var svc = new RecordingSessionService
        {
            CreateException = new SessionLimitExceededException("Running session limit reached (20 of 20).")
        };
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken);

        var result = await controller.Create(new CreateSessionRequest { Title = "x", Mode = SessionMode.Interactive }, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(429, status.StatusCode);
        Assert.Equal(svc.CreateException.Message, status.Value);
    }

    [Fact]
    public async Task SessionsCreate_WhenLimitExceeded_Returns429()
    {
        var svc = new RecordingSessionService
        {
            CreateException = new SessionLimitExceededException("Running session limit reached (20 of 20).")
        };
        var controller = Sessions(svc);

        var result = await controller.Create(new CreateSessionRequest { Title = "x", Mode = SessionMode.Interactive }, CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(429, status.StatusCode);
        Assert.Equal(svc.CreateException.Message, status.Value);
    }

    [Fact]
    public async Task SessionsDuplicate_WhenLimitExceeded_Returns429()
    {
        var svc = new RecordingSessionService
        {
            DuplicateException = new SessionLimitExceededException("Running session limit reached (20 of 20).")
        };
        var controller = Sessions(svc);

        var result = await controller.Duplicate("session-1",
            new DuplicateSessionRequest("copy", null, false), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(429, status.StatusCode);
        Assert.Equal(svc.DuplicateException.Message, status.Value);
    }

    [Fact]
    public async Task SessionsCreate_MapsGenericInvalidOperation_To409_Not429()
    {
        var svc = new RecordingSessionService
        {
            CreateException = new InvalidOperationException("other conflict")
        };
        var controller = Sessions(svc);

        var result = await controller.Create(new CreateSessionRequest { Title = "x", Mode = SessionMode.Interactive }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task PauseAndResume_PassTheTokenOwnerToTheService()
    {
        // Taking a conversation off the cluster and handing it back needs both steps on the token
        // surface; without them a client has to stop the pod through the web app, and the state it
        // downloads keeps moving underneath it.
        var svc = new RecordingSessionService();
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken);

        Assert.IsType<OkObjectResult>((await controller.Pause("session-1", CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await controller.Resume("session-1", CancellationToken.None)).Result);

        Assert.Equal([("alice", "session-1")], svc.PauseCalls);
        Assert.Equal([("alice", "session-1")], svc.ResumeCalls);
    }

    [Fact]
    public async Task PauseAndResume_WithoutAValidToken_AreUnauthorizedAndNeverReachTheService()
    {
        var svc = new RecordingSessionService();
        var controller = Remote((_, _) => Task.FromResult<string?>(null), svc, "oah_unknown");

        Assert.IsType<UnauthorizedResult>((await controller.Pause("session-1", CancellationToken.None)).Result);
        Assert.IsType<UnauthorizedResult>((await controller.Resume("session-1", CancellationToken.None)).Result);
        Assert.Empty(svc.PauseCalls);
        Assert.Empty(svc.ResumeCalls);
    }

    [Fact]
    public async Task Pause_MapsTheServiceOutcomesToStatusCodes()
    {
        var owner = (string _, CancellationToken _) => Task.FromResult<string?>("alice");

        Assert.IsType<NotFoundResult>((await Remote(owner,
            new RecordingSessionService { PauseException = new KeyNotFoundException() }, ValidToken)
            .Pause("session-1", CancellationToken.None)).Result);

        // A scheduled session runs on its schedule and has nothing to pause.
        var badRequest = Assert.IsType<BadRequestObjectResult>((await Remote(owner,
            new RecordingSessionService { PauseException = new ArgumentException("Scheduled sessions cannot be paused.") },
            ValidToken).Pause("session-1", CancellationToken.None)).Result);
        Assert.Equal("Scheduled sessions cannot be paused.", badRequest.Value);
    }

    [Fact]
    public async Task Resume_MapsTheServiceOutcomesToStatusCodes()
    {
        var owner = (string _, CancellationToken _) => Task.FromResult<string?>("alice");
        async Task<IActionResult?> Act(Exception thrown) => (await Remote(owner,
            new RecordingSessionService { ResumeException = thrown }, ValidToken)
            .Resume("session-1", CancellationToken.None)).Result;

        Assert.IsType<NotFoundResult>(await Act(new KeyNotFoundException()));
        Assert.IsType<BadRequestObjectResult>(await Act(new ArgumentException("Scheduled sessions are not resumed.")));
        Assert.IsType<ConflictObjectResult>(await Act(new InvalidOperationException("already running")));

        // The same 403 the in-app surface gives when the agent was removed from the allowlist
        // after the session was created.
        var forbidden = Assert.IsType<ObjectResult>(await Act(new AgentNotAllowedException("Codex is not allowed.")));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Convert_PassesTheTokenOwnerAndTheBodyToTheService()
    {
        var svc = new RecordingSessionService();
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken);

        var result = await controller.Convert("session-1",
            new ConvertSessionRequest { UiMode = "chat", AutoApprove = true, Resume = false }, CancellationToken.None);

        var info = Assert.IsType<SessionInfo>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(SessionMode.Interactive, info.Mode);
        var call = Assert.Single(svc.ConvertCalls);
        Assert.Equal(("alice", "session-1"), (call.Owner, call.Id));
        Assert.Equal("chat", call.Request.UiMode);
        Assert.True(call.Request.AutoApprove);
        Assert.False(call.Request.Resume);
    }

    [Fact]
    public async Task Convert_WithoutAValidToken_IsUnauthorizedAndNeverReachesTheService()
    {
        var svc = new RecordingSessionService();
        var controller = Remote((_, _) => Task.FromResult<string?>(null), svc, "oah_unknown");

        var result = await controller.Convert("session-1", new ConvertSessionRequest(), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.Empty(svc.ConvertCalls);
    }

    [Fact]
    public async Task Convert_MapsTheServiceOutcomesToStatusCodes()
    {
        var owner = (string _, CancellationToken _) => Task.FromResult<string?>("alice");
        async Task<IActionResult?> Act(Exception thrown) => (await Remote(owner,
            new RecordingSessionService { ConvertException = thrown }, ValidToken)
            .Convert("session-1", new ConvertSessionRequest(), CancellationToken.None)).Result;

        Assert.IsType<NotFoundResult>(await Act(new KeyNotFoundException()));

        // Not autonomous, or still running: wait or pause first. The body carries the reason so
        // a client can show it instead of guessing which of the two it was.
        var conflict = Assert.IsType<ConflictObjectResult>(await Act(new InvalidOperationException("Pause the session first.")));
        Assert.Equal("Pause the session first.", ErrorOf(conflict.Value));

        var bad = Assert.IsType<BadRequestObjectResult>(await Act(new ArgumentException("Chat UI mode is only supported for interactive Claude sessions.")));
        Assert.Contains("Claude", ErrorOf(bad.Value));

        var forbidden = Assert.IsType<ObjectResult>(await Act(new AgentNotAllowedException("Codex is not allowed.")));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task SessionsConvert_UsesTheSameStatusCodesAsTheRemoteSurface()
    {
        async Task<IActionResult?> Act(Exception thrown) => (await Sessions(
            new RecordingSessionService { ConvertException = thrown })
            .Convert("session-1", new ConvertSessionRequest(), CancellationToken.None)).Result;

        Assert.IsType<NotFoundResult>(await Act(new KeyNotFoundException()));
        var conflict = Assert.IsType<ConflictObjectResult>(await Act(new InvalidOperationException("The session is already interactive.")));
        Assert.Equal("The session is already interactive.", ErrorOf(conflict.Value));
        Assert.IsType<BadRequestObjectResult>(await Act(new ArgumentException("bad ui mode")));

        var svc = new RecordingSessionService();
        Assert.IsType<OkObjectResult>((await Sessions(svc).Convert("session-1", new ConvertSessionRequest(), CancellationToken.None)).Result);
        Assert.Equal(("alice", "session-1"), (svc.ConvertCalls[0].Owner, svc.ConvertCalls[0].Id));
    }

    /// <summary>The anonymous <c>{ error }</c> body the convert endpoints answer with.</summary>
    private static string? ErrorOf(object? value) =>
        value?.GetType().GetProperty("error")?.GetValue(value) as string;

    [Fact]
    public async Task State_WithoutAValidToken_IsUnauthorizedAndNeverTouchesTheArchive()
    {
        // The state archive is the whole conversation. An unauthenticated caller must not reach
        // the service at all, in either direction.
        var svc = new RecordingSessionService();
        var controller = Remote((_, _) => Task.FromResult<string?>(null), svc, "oah_unknown");

        Assert.IsType<UnauthorizedResult>(await controller.DownloadState("session-1", CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.UploadState("session-1", CancellationToken.None));
        Assert.Equal(0, svc.StateReads);
        Assert.Equal(0, svc.StateWrites);
    }

    [Fact]
    public async Task DownloadState_PassesTheTokenOwnerAndStreamsTheArchive()
    {
        var svc = new RecordingSessionService { StateArchive = [1, 2, 3] };
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken);

        var file = Assert.IsType<FileStreamResult>(
            await controller.DownloadState("session-1", CancellationToken.None));

        Assert.Equal("alice", svc.StateOwner);
        Assert.Equal("application/gzip", file.ContentType);
        Assert.Equal("session-1-state.tgz", file.FileDownloadName);
    }

    [Fact]
    public async Task DownloadState_NotFoundWhenNothingIsStored()
    {
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), new RecordingSessionService(), ValidToken);

        Assert.IsType<NotFoundResult>(await controller.DownloadState("session-1", CancellationToken.None));
    }

    [Fact]
    public async Task UploadState_MapsTheServiceOutcomesToStatusCodes()
    {
        var owner = (string _, CancellationToken _) => Task.FromResult<string?>("alice");

        Assert.IsType<NoContentResult>(await Remote(owner, new RecordingSessionService(), ValidToken)
            .UploadState("session-1", CancellationToken.None));

        Assert.IsType<NotFoundResult>(await Remote(owner,
            new RecordingSessionService { StateWriteException = new KeyNotFoundException() }, ValidToken)
            .UploadState("session-1", CancellationToken.None));

        Assert.IsType<ConflictObjectResult>(await Remote(owner,
            new RecordingSessionService { StateWriteException = new InvalidOperationException("still running") },
            ValidToken).UploadState("session-1", CancellationToken.None));

        var unavailable = Assert.IsType<ObjectResult>(await Remote(owner,
            new RecordingSessionService { StateWriteStored = false }, ValidToken)
            .UploadState("session-1", CancellationToken.None));
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, unavailable.StatusCode);
    }

    [Fact]
    public async Task Transcript_WithoutOffset_ReturnsWholePageAndRunningFlag()
    {
        var svc = new RecordingSessionService
        {
            Session = new SessionInfo
            {
                Id = "session-1", Title = "t", Owner = "alice",
                Mode = SessionMode.Interactive, Phase = "Running"
            },
            Transcript = "hello world"
        };
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken);

        var result = await controller.Transcript("session-1", null, null, CancellationToken.None);

        var page = Assert.IsType<TranscriptPage>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("hello world", page.Text);
        Assert.Equal(0, page.Offset);
        Assert.Equal(11, page.NextOffset);
        Assert.Equal(11, page.Length);
        Assert.True(page.Running);
        Assert.False(page.Truncated);
    }

    [Fact]
    public async Task Transcript_WithCursor_ReturnsOnlyWhatIsNewAndStopsPollingWhenFinished()
    {
        var svc = new RecordingSessionService
        {
            Session = new SessionInfo
            {
                Id = "session-1", Title = "t", Owner = "alice",
                Mode = SessionMode.Autonomous, Phase = "Succeeded"
            },
            Transcript = "first chunk|second chunk"
        };
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken);

        var result = await controller.Transcript("session-1", 12, null, CancellationToken.None);

        var page = Assert.IsType<TranscriptPage>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("second chunk", page.Text);
        Assert.Equal(24, page.NextOffset);
        Assert.False(page.Running);
    }

    [Fact]
    public async Task Transcript_ForAnotherOwnersSession_Returns404_WithoutReadingTheTranscript()
    {
        var svc = new RecordingSessionService { Session = null, Transcript = "leak me" };
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken);

        var result = await controller.Transcript("session-1", null, null, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(0, svc.TranscriptCalls);
    }

    [Fact]
    public async Task Transcript_WithInvalidToken_ReturnsUnauthorized()
    {
        var svc = new RecordingSessionService { Transcript = "secret" };
        var controller = Remote((_, _) => Task.FromResult<string?>(null), svc, "oah_unknown");

        var result = await controller.Transcript("session-1", null, null, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result.Result);
        Assert.Equal(0, svc.TranscriptCalls);
    }

    [Fact]
    public async Task SendMessage_StoresTheMessage_AndCountsAsActivityOnTheTarget()
    {
        // A task handed to a session is the owner using it; without the touch an idle-based
        // deadline would delete a session that was just given work.
        var svc = new RecordingSessionService
        {
            Session = new SessionInfo { Id = "session-1", Title = "t", Owner = "alice", Mode = SessionMode.Interactive, Phase = "Running" }
        };
        var messages = new RecordingMessageStore();
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken, messages);

        var result = await controller.SendMessage("session-1", new RemoteAgentMessageRequest("do the thing"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Single(messages.Added);
        Assert.Equal([("alice", "session-1")], svc.TouchCalls);
    }

    [Fact]
    public async Task SendMessage_ToAnUnknownSession_NeitherStoresNorTouches()
    {
        var svc = new RecordingSessionService { Session = null };
        var messages = new RecordingMessageStore();
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken, messages);

        var result = await controller.SendMessage("session-1", new RemoteAgentMessageRequest("do the thing"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(messages.Added);
        Assert.Empty(svc.TouchCalls);
    }

    [Fact]
    public async Task Update_PassesOnlyTheRemoteFieldsToTheService()
    {
        var svc = new RecordingSessionService();
        var controller = Remote((_, _) => Task.FromResult<string?>("alice"), svc, ValidToken);

        var result = await controller.Update("session-1",
            new RemoteUpdateSessionRequest { AutoDeleteAfterSeconds = 7200, AutoDeleteFrom = "start", Title = "renamed" },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        var (owner, id, request) = Assert.Single(svc.UpdateCalls);
        Assert.Equal(("alice", "session-1"), (owner, id));
        Assert.Equal(7200, request.AutoDeleteAfterSeconds);
        Assert.Equal("start", request.AutoDeleteFrom);
        Assert.Equal("renamed", request.Title);
        // Nothing a token should not be able to touch rides along.
        Assert.Null(request.Image);
        Assert.Null(request.RunAsRoot);
        Assert.Null(request.Repos);
    }

    [Fact]
    public async Task Update_MapsTheServiceOutcomesToStatusCodes()
    {
        var owner = (string _, CancellationToken _) => Task.FromResult<string?>("alice");
        var body = new RemoteUpdateSessionRequest { AutoDeleteAfterSeconds = 60 };

        Assert.IsType<NotFoundResult>((await Remote(owner,
            new RecordingSessionService { UpdateException = new KeyNotFoundException() }, ValidToken)
            .Update("session-1", body, CancellationToken.None)).Result);
        var badRequest = Assert.IsType<BadRequestObjectResult>((await Remote(owner,
            new RecordingSessionService { UpdateException = new ArgumentException("too short") }, ValidToken)
            .Update("session-1", body, CancellationToken.None)).Result);
        Assert.Equal("too short", badRequest.Value);

        var svc = new RecordingSessionService();
        Assert.IsType<UnauthorizedResult>((await Remote((_, _) => Task.FromResult<string?>(null), svc, "oah_unknown")
            .Update("session-1", body, CancellationToken.None)).Result);
        Assert.Empty(svc.UpdateCalls);
    }

    // ------------------------------------------------------------------ fixtures

    private sealed class RecordingMessageStore : ISessionMessageStore
    {
        public List<SessionMessageRecord> Added { get; } = [];
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task AddAsync(SessionMessageRecord message, CancellationToken ct = default)
        {
            Added.Add(message);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SessionMessageRecord>> TakeUndeliveredAsync(string toSessionId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionMessageRecord>>([]);
        public Task<IReadOnlyList<SessionMessageRecord>> ListRecentAsync(string toSessionId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionMessageRecord>>([]);
    }

    private static RemoteController Remote(
        Func<string, CancellationToken, Task<string?>> findOwner,
        RecordingSessionService svc,
        string? bearerToken,
        ISessionMessageStore? messages = null)
    {
        var controller = new RemoteController(findOwner, svc, messages)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        if (bearerToken is not null)
            controller.Request.Headers.Authorization = $"Bearer {bearerToken}";
        return controller;
    }

    private static SessionsController Sessions(RecordingSessionService svc)
    {
        var controller = new SessionsController(svc, null!, [])
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("preferred_username", "alice")], "test"));
        return controller;
    }

    private sealed class RecordingSessionService : ISessionService
    {
        public int DeleteCalls { get; private set; }
        public string? DeleteOwner { get; private set; }
        public string? DeleteId { get; private set; }
        public Exception? CreateException { get; init; }
        public Exception? DuplicateException { get; init; }
        public byte[]? StateArchive { get; init; }
        public Exception? StateWriteException { get; init; }
        public bool StateWriteStored { get; init; } = true;
        public int StateReads { get; private set; }
        public int StateWrites { get; private set; }
        public string? StateOwner { get; private set; }

        public Task<Stream?> OpenStateArchiveAsync(string owner, string id, CancellationToken ct = default)
        {
            StateReads++;
            StateOwner = owner;
            return Task.FromResult<Stream?>(StateArchive is null ? null : new MemoryStream(StateArchive));
        }

        public Task<bool> ReplaceStateArchiveAsync(string owner, string id, Stream content,
            long? contentLength, CancellationToken ct = default)
        {
            StateWrites++;
            StateOwner = owner;
            if (StateWriteException is not null) throw StateWriteException;
            return Task.FromResult(StateWriteStored);
        }

        public SessionInfo? Session { get; init; }
        public string? Transcript { get; init; }
        public int TranscriptCalls { get; private set; }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
        {
            if (CreateException is not null) throw CreateException;
            return Task.FromResult(new SessionInfo { Id = "new", Title = req.Title, Owner = owner, Mode = req.Mode, Phase = "Pending" });
        }

        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default)
        {
            if (DuplicateException is not null) throw DuplicateException;
            return Task.FromResult(new SessionInfo { Id = "dup", Title = "t", Owner = owner, Mode = SessionMode.Interactive, Phase = "Pending" });
        }

        public Exception? PauseException { get; init; }
        public Exception? ResumeException { get; init; }
        public List<(string Owner, string Id)> PauseCalls { get; } = [];
        public List<(string Owner, string Id)> ResumeCalls { get; } = [];

        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default)
        {
            ResumeCalls.Add((owner, id));
            if (ResumeException is not null) throw ResumeException;
            return Task.FromResult(Info(owner, id, SessionStatus.Pending));
        }

        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default)
        {
            PauseCalls.Add((owner, id));
            if (PauseException is not null) throw PauseException;
            return Task.FromResult(Info(owner, id, SessionStatus.Paused));
        }

        private static SessionInfo Info(string owner, string id, string phase) => new()
        {
            Id = id, Title = "t", Owner = owner, Mode = SessionMode.Interactive, Phase = phase
        };
        public Exception? UpdateException { get; init; }
        public List<(string Owner, string Id, UpdateSessionRequest Request)> UpdateCalls { get; } = [];
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default)
        {
            UpdateCalls.Add((owner, id, req));
            if (UpdateException is not null) throw UpdateException;
            return Task.FromResult(Info(owner, id, SessionStatus.Running));
        }

        public Exception? ConvertException { get; init; }
        public List<(string Owner, string Id, ConvertSessionRequest Request)> ConvertCalls { get; } = [];

        public Task<SessionInfo> ConvertSessionAsync(string owner, string id, ConvertSessionRequest req, CancellationToken ct = default)
        {
            ConvertCalls.Add((owner, id, req));
            if (ConvertException is not null) throw ConvertException;
            return Task.FromResult(Info(owner, id, SessionStatus.Pending) with { ConvertedFrom = SessionMode.Autonomous });
        }
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(Session);
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public List<(string Owner, string Id)> TouchCalls { get; } = [];
        public Task TouchActivityAsync(string owner, string id, CancellationToken ct = default)
        {
            TouchCalls.Add((owner, id));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Mirrors production: the real service returns null for a session the owner cannot see, so
        /// the double must not hand out a transcript just because one was configured.
        /// </summary>
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default)
        {
            TranscriptCalls++;
            return Task.FromResult(Session is null ? null : Transcript);
        }
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
