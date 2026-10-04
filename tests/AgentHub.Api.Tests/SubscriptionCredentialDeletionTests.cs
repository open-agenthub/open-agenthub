using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Subscription logins are captured from a session by its runtime and uploaded, never typed in.
/// Until this endpoint existed a login that stopped working — a truncated paste, an expired
/// token — was restored into every new session with nothing in the product able to clear it.
/// </summary>
public class SubscriptionCredentialDeletionTests
{
    [Theory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Cursor)]
    [InlineData(AgentKind.OpenClaw)]
    public async Task Delete_ForgetsTheLoginOfTheCallerForThatAgent(AgentKind agent)
    {
        var service = new RecordingSessionService();
        var controller = Controller(service, "alice");

        var result = await controller.DeleteSubscription(agent, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal([("alice", agent)], service.Deleted);
    }

    /// <summary>
    /// The owner comes from the token, never from the route, or one account could clear another's
    /// stored logins.
    /// </summary>
    [Fact]
    public async Task Delete_IsScopedToTheAuthenticatedCaller()
    {
        var service = new RecordingSessionService();

        await Controller(service, "alice").DeleteSubscription(AgentKind.OpenClaw, CancellationToken.None);
        await Controller(service, "bob").DeleteSubscription(AgentKind.OpenClaw, CancellationToken.None);

        Assert.Equal(
            [("alice", AgentKind.OpenClaw), ("bob", AgentKind.OpenClaw)],
            service.Deleted);
    }

    /// <summary>
    /// Idempotent, and deliberately indistinguishable from a successful delete: a 404 for "no
    /// login stored" would let a caller enumerate which providers an account has signed in to.
    /// </summary>
    [Fact]
    public async Task Delete_AnswersNoContentWhenNothingWasStored()
    {
        var service = new RecordingSessionService { ThrowNotFound = false };
        var controller = Controller(service, "alice");

        var first = await controller.DeleteSubscription(AgentKind.Cursor, CancellationToken.None);
        var second = await controller.DeleteSubscription(AgentKind.Cursor, CancellationToken.None);

        Assert.IsType<NoContentResult>(first);
        Assert.IsType<NoContentResult>(second);
        Assert.Equal(2, service.Deleted.Count);
    }

    [Fact]
    public async Task Delete_RejectsAnAgentOutsideTheEnum()
    {
        var service = new RecordingSessionService();
        var controller = Controller(service, "alice");

        var result = await controller.DeleteSubscription((AgentKind)999, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(service.Deleted);
    }

    private static CredentialsController Controller(ISessionService service, string owner)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", owner)], "test"));
        return new CredentialsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
    }

    private sealed class RecordingSessionService : ISessionService
    {
        public List<(string Owner, AgentKind Agent)> Deleted { get; } = [];
        public bool ThrowNotFound { get; init; }

        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default)
        {
            Deleted.Add((owner, agent));
            return Task.CompletedTask;
        }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult(new CredentialStatus());
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
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
