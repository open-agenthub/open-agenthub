using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public interface ISessionService
{
    Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default);
    /// <summary>Which credential fields have a stored value (never the values themselves).</summary>
    Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default);
    /// <summary>Persists a user's provider CLI subscription credentials so new sessions
    /// can start without another login.</summary>
    Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default);

    /// <summary>
    /// Forgets a stored provider login. Nothing else could: the runtimes capture these from a
    /// session's own state and upload them, so a credential that stopped working — a truncated
    /// paste, an expired token — was restored into every new session with no way to clear it
    /// short of deleting the Secret by hand.
    ///
    /// Idempotent: a caller should not have to check first, and the status code must not reveal
    /// whether a login existed.
    /// </summary>
    Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default);

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
    /// <summary>The terminal scrollback as plain text, control sequences stripped — what people read.</summary>
    Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default);
    /// <summary>
    /// The terminal scrollback exactly as the agent uploaded it, for a resuming pod to seed its
    /// buffer with. Separate from <see cref="GetTranscriptAsync"/> because the resume used to be
    /// fed the stripped text and re-persist it as raw, degrading the history on every restart.
    /// Default null for test doubles that never resume anything.
    /// </summary>
    Task<string?> GetScrollbackAsync(string owner, string id, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
    /// <summary>
    /// The conversation as the provider recorded it — user, assistant, tool turns — read from
    /// the native transcript the session agent uploads. Null when the session is unknown or no
    /// native transcript exists (a session older than this feature, or a runtime without one),
    /// in which case callers fall back to <see cref="GetTranscriptAsync"/>. Default null for
    /// test doubles.
    /// </summary>
    Task<IReadOnlyList<TranscriptEntry>?> GetConversationAsync(string owner, string id, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<TranscriptEntry>?>(null);
    /// <summary>Opens the stored provider state archive — the same tar.gz a resuming pod unpacks
    /// into its home directory, and therefore the conversation history an agent CLI needs to
    /// continue the session off-cluster. Null when the session is unknown or nothing is stored.</summary>
    Task<Stream?> OpenStateArchiveAsync(string owner, string id, CancellationToken ct = default)
        => Task.FromResult<Stream?>(null);
    /// <summary>Overwrites the stored state archive so the next resume continues a conversation
    /// that ran elsewhere. False when no object storage is configured.</summary>
    Task<bool> ReplaceStateArchiveAsync(string owner, string id, Stream content,
        long? contentLength, CancellationToken ct = default) => Task.FromResult(false);
    Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default);
    Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default);
    /// <summary>Deletes every per-user Kubernetes secret (credentials, provider logins,
    /// git OAuth tokens) — part of the account purge. Default no-op for test doubles.</summary>
    Task DeleteUserSecretsAsync(string owner, CancellationToken ct = default) => Task.CompletedTask;
}
