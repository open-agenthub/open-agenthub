using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public interface ISessionService
{
    Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default);
    /// <summary>Persists a user's Claude CLI OAuth credentials (subscription login),
    /// so new sessions start without requiring another login.</summary>
    Task StoreClaudeCredentialsAsync(string owner, string credentialsJson, CancellationToken ct = default);
    Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default);
    Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default);
    Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default);
    Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default);
    Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default);
    Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default);
    Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default);
}
