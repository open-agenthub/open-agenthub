using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public interface ISessionService
{
    Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default);
    /// <summary>Which credential fields have a stored value (never the values themselves).</summary>
    Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default);
    /// <summary>Persists a user's Claude CLI OAuth credentials (subscription login),
    /// so new sessions start without requiring another login.</summary>
    Task StoreClaudeCredentialsAsync(string owner, string credentialsJson, CancellationToken ct = default);
    Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default);
    Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default);
    Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default);
    /// <summary>Pauses a running session: uploads its state, removes the pod and marks it "Paused".
    /// A paused session is resumable via the normal resume path.</summary>
    Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default);
    /// <summary>Partial update of session settings; non-title changes apply on the next resume.</summary>
    Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default);
    Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default);
    /// <summary>Clears the "waiting for reply" flag (e.g. once the user opens the terminal).</summary>
    Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default);
    Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default);
    Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default);
    Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default);
}
