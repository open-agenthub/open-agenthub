using System.Text;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public class CredentialSelectionTests
{
    [Fact]
    public void CredentialKeysAndProviderSecretsAreProviderSpecific()
    {
        Assert.Equal("openai_api_key", KubernetesSessionService.CredentialKey(nameof(UserCredentials.OpenAiApiKey)));
        Assert.Equal("claude-u-2bd806c97f0e00af", KubernetesSessionService.ProviderSecretName("alice", AgentKind.Claude));
        Assert.Equal("codex-u-2bd806c97f0e00af", KubernetesSessionService.ProviderSecretName("alice", AgentKind.Codex));
    }

    [Fact]
    public async Task ProviderCredentials_AcceptsAuthenticatedMatchingCodexSubscription()
    {
        var service = new RecordingSessionService();
        var controller = Controller(new SessionRecord
        {
            Id = "session-1", Owner = "alice", CallbackToken = "callback-token",
            Agent = AgentKind.Codex, AuthMode = AgentAuthMode.Subscription
        }, service, "{\"tokens\":{\"access_token\":\"test-token\"}}");

        var result = await controller.ProviderCredentials("session-1", "codex", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal("alice", service.Owner);
        Assert.Equal(AgentKind.Codex, service.Agent);
        Assert.Equal(1, service.StoreCalls);
    }

    [Theory]
    [InlineData(AgentKind.Claude, AgentAuthMode.Subscription, "codex")]
    [InlineData(AgentKind.Codex, AgentAuthMode.ApiKey, "codex")]
    public async Task ProviderCredentials_RejectsProviderMismatchOrApiKeySessions(
        AgentKind sessionAgent, AgentAuthMode authMode, string routeAgent)
    {
        var service = new RecordingSessionService();
        var controller = Controller(new SessionRecord
        {
            Id = "session-1", Owner = "alice", CallbackToken = "callback-token",
            Agent = sessionAgent, AuthMode = authMode
        }, service, "{\"tokens\":{}}" );

        var result = await controller.ProviderCredentials("session-1", routeAgent, CancellationToken.None);

        Assert.IsType<ConflictResult>(result);
        Assert.Equal(0, service.StoreCalls);
    }

    [Fact]
    public async Task ProviderCredentials_RejectsInvalidJsonWithoutStoringIt()
    {
        var service = new RecordingSessionService();
        var controller = Controller(new SessionRecord
        {
            Id = "session-1", Owner = "alice", CallbackToken = "callback-token",
            Agent = AgentKind.Codex, AuthMode = AgentAuthMode.Subscription
        }, service, "not-json");

        var result = await controller.ProviderCredentials("session-1", "codex", CancellationToken.None);

        Assert.IsType<BadRequestResult>(result);
        Assert.Equal(0, service.StoreCalls);
    }

    private static InternalController Controller(SessionRecord session, RecordingSessionService service, string body)
    {
        var controller = new InternalController(new CallbackSessionStore(session), [], service, null!, null!, null!);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.Request.Headers["X-Agent-Token"] = session.CallbackToken;
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return controller;
    }

    private sealed class CallbackSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default)
            => Task.FromResult<SessionRecord?>(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingSessionService : ISessionService
    {
        public int StoreCalls { get; private set; }
        public string? Owner { get; private set; }
        public AgentKind Agent { get; private set; }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => Task.FromResult(new CredentialStatus());
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default)
        {
            StoreCalls++;
            Owner = owner;
            Agent = agent;
            return Task.CompletedTask;
        }
        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
