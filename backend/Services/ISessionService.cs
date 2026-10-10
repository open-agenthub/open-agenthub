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

    /// <summary>Stores a git PAT for a host, or rotates the one already stored for that host.
    /// Throws <see cref="ArgumentException"/> for an invalid kind, host or token.</summary>
    Task<GitPatInfo> UpsertGitPatAsync(string owner, UpsertGitPatRequest request, CancellationToken ct = default)
        => throw new NotSupportedException();
    /// <summary>Removes a stored git PAT by id. Idempotent: an unknown id is not an error.</summary>
    Task DeleteGitPatAsync(string owner, string id, CancellationToken ct = default)
        => throw new NotSupportedException();

    /// <summary>
    /// Stores a login a session pod uploaded, into the account it belongs to — the one the session
    /// had mounted, one with the same identity, or a new one (docs/provider-accounts.md). Returns
    /// the account id the session should be attached to from now on. The default forwards to
    /// <see cref="StoreProviderCredentialsAsync"/> so a test double without accounts keeps working.
    /// </summary>
    async Task<string?> StoreProviderLoginAsync(string owner, AgentKind agent, string json,
        ProviderAccountIdentity? identity, string? mountedCredentialId, CancellationToken ct = default)
    {
        await StoreProviderCredentialsAsync(owner, agent, json, ct);
        return mountedCredentialId;
    }

    /// <summary>Every stored provider account of the owner, keyed by agent name. Never the files.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<ProviderAccountInfo>>> ListProviderAccountsAsync(
        string owner, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>Renames an account or makes it the default. Null when the account does not exist.</summary>
    Task<ProviderAccountInfo?> UpdateProviderAccountAsync(string owner, AgentKind agent, string id,
        UpdateProviderAccountRequest req, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>Forgets one account. Idempotent, like <see cref="DeleteProviderCredentialsAsync"/>.</summary>
    Task DeleteProviderAccountAsync(string owner, AgentKind agent, string id, CancellationToken ct = default)
        => throw new NotSupportedException();

    /// <summary>
    /// Moves a running Subscription session to another of the owner's accounts: records the choice
    /// and hands the file to the pod, which restarts its agent with resume. Throws
    /// <see cref="KeyNotFoundException"/> (no such session), <see cref="ArgumentException"/> (no
    /// such account, or a session this does not apply to), <see cref="InvalidOperationException"/>
    /// (not running) or <see cref="HttpRequestException"/> (the pod did not take the file).
    /// </summary>
    Task<SessionInfo> SwitchSessionCredentialAsync(string owner, string id, string credentialId,
        CancellationToken ct = default) => throw new NotSupportedException();

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
    /// <summary>
    /// Records that the owner (or someone acting for them) used the session, for the idle
    /// countdown of docs/session-expiry.md. Owner-checked like <see cref="ClearQuestionAsync"/>;
    /// a no-op for an unknown session. Default no-op for test doubles.
    /// </summary>
    Task TouchActivityAsync(string owner, string id, CancellationToken ct = default) => Task.CompletedTask;
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
