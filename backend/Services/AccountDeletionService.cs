using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;

namespace AgentHub.Api.Services;

/// <summary>
/// GDPR account deletion: removes every trace of a user — running sessions (pods,
/// browser state, files), all database rows (see <see cref="AccountPurgeStore"/> for
/// the table inventory), the per-user Kubernetes secrets and the S3 objects. The
/// identity itself lives in the external OIDC provider and is not touched; signing
/// in again simply starts a fresh, empty account. Idempotent — a retry after a
/// partial failure finishes the remainder.
/// </summary>
public sealed class AccountDeletionService
{
    private readonly ISessionService _sessions;
    private readonly IAccountPurgeStore _purge;
    private readonly IArtifactStore _artifacts;
    private readonly ILogger<AccountDeletionService> _log;

    public AccountDeletionService(ISessionService sessions, IAccountPurgeStore purge,
        IArtifactStore artifacts, ILogger<AccountDeletionService> log)
    {
        _sessions = sessions;
        _purge = purge;
        _artifacts = artifacts;
        _log = log;
    }

    public async Task DeleteAccountAsync(string owner, CancellationToken ct = default)
    {
        // 1. Session ids first — the database purge needs them after the rows are gone.
        var sessionIds = await _purge.ListSessionIdsAsync(owner, ct);

        // 2. Tear down each session (pod, cronjob, browser, session files, MCP secrets).
        //    One stuck session must not block the rest — the purge sweeps its rows anyway.
        foreach (var id in sessionIds)
        {
            try { await _sessions.DeleteSessionAsync(owner, id, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogWarning(ex, "Deleting session {Id} during account purge failed; continuing", id); }
        }

        // 3. All database rows; returns the owner's skill ids for S3 content cleanup.
        var skillIds = await _purge.PurgeAsync(owner, sessionIds, ct);

        // 4. Per-user Kubernetes secrets (credentials, provider logins, git OAuth).
        await _sessions.DeleteUserSecretsAsync(owner, ct);

        // 5. S3 objects: session state/transcripts/artifacts live under the hashed owner
        //    key, session files under the escaped raw owner (see IArtifactStore key layout).
        await _artifacts.DeleteByPrefixAsync($"sessions/{KubernetesSessionService.OwnerKey(owner)}/", ct);
        await _artifacts.DeleteByPrefixAsync($"sessions/{Uri.EscapeDataString(owner)}/", ct);
        foreach (var skillId in skillIds)
            await _artifacts.DeleteByPrefixAsync($"skills/{skillId}/", ct);

        _log.LogInformation("Account deleted ({Sessions} sessions, {Skills} skills)", sessionIds.Count, skillIds.Count);
    }
}
