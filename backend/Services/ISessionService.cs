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

    /// <summary>The owner's stored secrets (docs/session-secrets.md): names, delivery and
    /// description, never a value. Default empty for test doubles that store none.</summary>
    Task<IReadOnlyList<SessionSecretInfo>> ListSessionSecretsAsync(string owner, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SessionSecretInfo>>(Array.Empty<SessionSecretInfo>());
    /// <summary>Stores a secret or updates the one with the same name, then brings every running
    /// session that receives it up to date. Throws <see cref="ArgumentException"/> for an invalid
    /// name, delivery or value.</summary>
    Task<SessionSecretInfo> UpsertSessionSecretAsync(string owner, UpsertSessionSecretRequest request,
        CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Removes a secret, from the store and from every running session. Idempotent.</summary>
    Task DeleteSessionSecretAsync(string owner, string id, CancellationToken ct = default)
        => throw new NotSupportedException();
    /// <summary>
    /// The secret ids a session in <paramref name="projectId"/> with the given selection receives
    /// right now — what a token scope and a spawning parent are checked against. Lenient, like a
    /// spawn: removed ids and ids outside the project drop out rather than throw, because it also
    /// answers for stored selections; a create request is validated strictly on its own. Default
    /// empty, so a test double without secrets hands none on.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveSessionSecretIdsAsync(string owner, string? projectId,
        IReadOnlyList<string>? requested, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    /// <summary>Rewrites the secret copies of the owner's sessions from the current store, project
    /// and session selections — after a project's selection changed. Default no-op.</summary>
    Task SyncSessionSecretsAsync(string owner, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>Refuses a project selection under which the project, or one of its sessions, would
    /// receive two secrets landing in the same place (#253). <paramref name="projectId"/> is null
    /// for a project being created. Default no-op, for test doubles without secrets.</summary>
    Task EnsureProjectSecretsFitAsync(string owner, string? projectId, string projectName,
        IReadOnlyList<string> secretIds, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>The text with the values of the secrets this session holds masked, for messages a
    /// hook sends on to the chat relays. Default unchanged, for test doubles without secrets.</summary>
    Task<string> MaskSessionSecretsAsync(string sessionId, string text, CancellationToken ct = default)
        => Task.FromResult(text);
    /// <summary>What the session receives right now, grants included — the ceiling a spawned child
    /// is held to. Default: the selection alone, for test doubles without grants.</summary>
    Task<IReadOnlyList<string>> SessionSecretIdsAsync(string owner, Persistence.SessionRecord session,
        CancellationToken ct = default)
        => ResolveSessionSecretIdsAsync(owner, session.ProjectId, SessionSecretSelection.Parse(session.SecretIdsJson), ct);
    /// <summary>
    /// Gives a session a stored secret in answer to its own request, and delivers it at once where
    /// that is possible. Throws <see cref="KeyNotFoundException"/> (no such session),
    /// <see cref="ArgumentException"/> (no such secret) or <see cref="CredentialScopeException"/>
    /// (outside the limit the session was created with).
    /// </summary>
    Task<SessionSecretGrant> GrantSessionSecretAsync(string owner, string sessionId, string secretId,
        CancellationToken ct = default) => throw new NotSupportedException();

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
    /// Records that an account hit its usage limit until <paramref name="until"/>
    /// (docs/account-limits.md). Null when the account does not exist. Default throws so the
    /// test doubles that never see a limit keep compiling.
    /// </summary>
    Task<ProviderAccountInfo?> MarkProviderAccountExhaustedAsync(string owner, AgentKind agent, string id,
        DateTime until, string? reason, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>
    /// Moves a running Subscription session to another of the owner's accounts: records the choice
    /// and hands the file to the pod, which restarts its agent with resume. Throws
    /// <see cref="KeyNotFoundException"/> (no such session), <see cref="ArgumentException"/> (no
    /// such account, or a session this does not apply to), <see cref="InvalidOperationException"/>
    /// (not running) or <see cref="HttpRequestException"/> (the pod did not take the file).
    /// </summary>
    Task<SessionInfo> SwitchSessionCredentialAsync(string owner, string id, string credentialId,
        CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>
    /// The same switch with a reason the pod shows in place of its generic "account switched"
    /// line — what the automatic failover passes (docs/account-limits.md). Default forwards to
    /// the plain switch so a test double implementing that one still serves both.
    /// </summary>
    Task<SessionInfo> SwitchSessionCredentialAsync(string owner, string id, string credentialId, string? reason,
        CancellationToken ct = default) => SwitchSessionCredentialAsync(owner, id, credentialId, ct);

    /// <summary>
    /// Stores which git tokens a session uses and installs them in its pod when it is running
    /// (docs/git-credentials.md). Throws <see cref="KeyNotFoundException"/> (no such session) or
    /// <see cref="ArgumentException"/> (unknown id, two tokens for one host).
    /// </summary>
    Task<GitPatSwitchResult> SwitchSessionGitPatsAsync(string owner, string id, IReadOnlyList<string>? gitPatIds,
        CancellationToken ct = default) => throw new NotSupportedException();

    Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default);
    Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default);
    Task<SessionInfo> UpdateRuntimeImageAsync(string owner, string id, CancellationToken ct = default)
        => throw new NotSupportedException();
    Task<SessionInfo> PauseAndResumeRuntimeImageAsync(string owner, string id, CancellationToken ct = default)
        => throw new NotSupportedException();
    /// <summary>Restarts a running interactive session (pause, resume) so its pod gets the stored
    /// CPU/memory; a no-op when it already has them (docs/session-resources.md).</summary>
    Task<SessionInfo> ApplyResourcesAsync(string owner, string id, CancellationToken ct = default)
        => throw new NotSupportedException();
    Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default);
    /// <summary>Pauses a running session: uploads its state, removes the pod and marks it "Paused".
    /// A paused session is resumable via the normal resume path.</summary>
    Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default);
    /// <summary>
    /// Continues a finished or paused autonomous session as an interactive one, by rewriting the
    /// record and resuming it (docs/session-mode-conversion.md). Throws
    /// <see cref="KeyNotFoundException"/> (no such session), <see cref="ArgumentException"/>
    /// (unknown target mode, UI mode the agent does not support) or
    /// <see cref="InvalidOperationException"/> (not autonomous, or not finished/paused). Default
    /// throws so the many test doubles that never convert keep compiling.
    /// </summary>
    Task<SessionInfo> ConvertSessionAsync(string owner, string id, ConvertSessionRequest req,
        CancellationToken ct = default) => throw new NotSupportedException();
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
    /// <summary>
    /// The provider's transcript file as the agent uploaded it (the S3 copy joined with the newer
    /// Postgres tail), for a resuming pod whose restored file may be older (#146). Default null
    /// for test doubles that never resume anything.
    /// </summary>
    Task<string?> GetTranscriptFileAsync(string owner, string id, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
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
    /// <summary>Fresh upload urls for the state archive, scrollback and transcript, for a pod whose
    /// start-time urls are about to expire. Null when the token does not belong to the session.</summary>
    Task<AgentSessionArtifactUrls?> RefreshArtifactUrlsAsync(string sessionId, string token, CancellationToken ct = default)
        => Task.FromResult<AgentSessionArtifactUrls?>(null);
    Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default);
    /// <summary>Deletes every per-user Kubernetes secret (credentials, provider logins,
    /// git OAuth tokens) — part of the account purge. Default no-op for test doubles.</summary>
    Task DeleteUserSecretsAsync(string owner, CancellationToken ct = default) => Task.CompletedTask;
}
