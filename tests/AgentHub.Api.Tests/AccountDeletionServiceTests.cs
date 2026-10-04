using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class AccountDeletionServiceTests
{
    private sealed class FakePurgeStore : IAccountPurgeStore
    {
        public List<string> SessionIds { get; init; } = new();
        public List<string> SkillIds { get; init; } = new();
        public (string Owner, IReadOnlyCollection<string> Sessions)? PurgeCall { get; private set; }

        public Task<IReadOnlyList<string>> ListSessionIdsAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(SessionIds);

        public Task<IReadOnlyList<string>> PurgeAsync(string owner, IReadOnlyCollection<string> sessionIds, CancellationToken ct = default)
        {
            PurgeCall = (owner, sessionIds);
            return Task.FromResult<IReadOnlyList<string>>(SkillIds);
        }
    }

    private sealed class RecordingArtifactStore : IArtifactStore
    {
        public List<string> DeletedPrefixes { get; } = new();
        public string PresignPut(string key, TimeSpan ttl) => "";
        public string PresignGet(string key, TimeSpan ttl) => "";
        public Task<string?> GetTextAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteByPrefixAsync(string prefix, CancellationToken ct = default)
        {
            DeletedPrefixes.Add(prefix);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSessionService : ISessionService
    {
        public List<string> DeletedSessions { get; } = new();
        public List<string> SecretOwners { get; } = new();
        public Func<string, bool> FailFor { get; init; } = _ => false;

        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default)
        {
            if (FailFor(id)) throw new InvalidOperationException($"session {id} is stuck");
            DeletedSessions.Add(id);
            return Task.CompletedTask;
        }

        public Task DeleteUserSecretsAsync(string owner, CancellationToken ct = default)
        {
            SecretOwners.Add(owner);
            return Task.CompletedTask;
        }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => Task.FromResult(new CredentialStatus());
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => Task.CompletedTask;
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
    }

    private static AccountDeletionService Service(RecordingSessionService sessions, FakePurgeStore purge, RecordingArtifactStore artifacts)
        => new(sessions, purge, artifacts, NullLogger<AccountDeletionService>.Instance);

    [Fact]
    public async Task Deletes_sessions_rows_secrets_and_storage()
    {
        var sessions = new RecordingSessionService();
        var purge = new FakePurgeStore { SessionIds = ["s1", "s2"], SkillIds = ["skill-a"] };
        var artifacts = new RecordingArtifactStore();

        await Service(sessions, purge, artifacts).DeleteAccountAsync("alice");

        Assert.Equal(new[] { "s1", "s2" }, sessions.DeletedSessions);
        Assert.Equal("alice", purge.PurgeCall!.Value.Owner);
        Assert.Equal(new[] { "s1", "s2" }, purge.PurgeCall.Value.Sessions);
        Assert.Equal(new[] { "alice" }, sessions.SecretOwners);

        // Hashed owner prefix (state/transcripts/artifacts), raw escaped owner prefix
        // (session files) and every purged skill's content.
        Assert.Contains($"sessions/{KubernetesSessionService.OwnerKey("alice")}/", artifacts.DeletedPrefixes);
        Assert.Contains("sessions/alice/", artifacts.DeletedPrefixes);
        Assert.Contains("skills/skill-a/", artifacts.DeletedPrefixes);
    }

    [Fact]
    public async Task A_stuck_session_does_not_abort_the_purge()
    {
        var sessions = new RecordingSessionService { FailFor = id => id == "s1" };
        var purge = new FakePurgeStore { SessionIds = ["s1", "s2"] };
        var artifacts = new RecordingArtifactStore();

        await Service(sessions, purge, artifacts).DeleteAccountAsync("alice");

        Assert.Equal(new[] { "s2" }, sessions.DeletedSessions);
        Assert.NotNull(purge.PurgeCall);          // rows swept regardless
        Assert.Single(sessions.SecretOwners);     // secrets deleted regardless
    }

    [Fact]
    public async Task Escapes_the_raw_owner_in_the_storage_prefix()
    {
        var sessions = new RecordingSessionService();
        var purge = new FakePurgeStore();
        var artifacts = new RecordingArtifactStore();

        await Service(sessions, purge, artifacts).DeleteAccountAsync("alice@example.com");

        Assert.Contains("sessions/alice%40example.com/", artifacts.DeletedPrefixes);
    }
}
