using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

namespace AgentHub.Api.Containers;

/// <summary>
/// Removes helper namespaces whose session no longer runs. Pause and delete clean up directly;
/// this catches what they could not — a backend restarted mid-delete, a session that ended on
/// its own, a cluster call that failed — so a forgotten database does not hold quota forever.
/// </summary>
public sealed class SessionContainerReconcileService(
    SessionContainerService containers,
    ISessionStore sessions,
    ILogger<SessionContainerReconcileService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!containers.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            // Any failure of one pass (API server unreachable, a timed-out call, a store error) must
            // not end the loop: the next pass retries, and only shutdown stops it.
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(error, "Session container reconciliation failed");
            }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        foreach (var sessionId in await containers.ListSessionNamespacesAsync(ct))
        {
            var session = await sessions.GetByIdAsync(sessionId, ct);
            if (session is not null && session.Status is SessionStatus.Pending or SessionStatus.Running) continue;
            log.LogInformation("Removing the helper containers of session {Session} ({Status})",
                sessionId, session?.Status ?? "deleted");
            await containers.CleanupSessionAsync(sessionId, ct);
        }
    }
}
