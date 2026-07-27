using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
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

    // ------------------------------------------------------------------ fixtures

    private static RemoteController Remote(
        Func<string, CancellationToken, Task<string?>> findOwner,
        RecordingSessionService svc,
        string? bearerToken)
    {
        var controller = new RemoteController(findOwner, svc)
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

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) =>
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

        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
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
