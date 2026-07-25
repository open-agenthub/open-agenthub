using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

namespace AgentHub.Api.Browser;

public sealed class BrowserReconcileService(
    IConfiguration configuration,
    IBrowserLeaseStore leases,
    ISessionStore sessions,
    IBrowserClusterClient cluster,
    IBrowserService browsers,
    IBrowserSessionLock sessionLock,
    ILogger<BrowserReconcileService> log) : BackgroundService
{
    private readonly string _namespace = configuration["AgentHub:Namespace"] ?? "agenthub-sessions";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ReconcileAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { log.LogError(error, "Browser reconciliation failed"); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        var currentLeases = await leases.ListAsync(ct);
        foreach (var lease in currentLeases)
        {
            var session = await sessions.GetByIdAsync(lease.SessionId, ct);
            var sessionIsLive = session is not null &&
                session.Status is SessionStatus.Pending or SessionStatus.Running;
            if (!sessionIsLive || lease.Phase == BrowserPhase.Stopping)
            {
                await browsers.StopAsync(lease.SessionId, ct);
                continue;
            }

            await using var distributedLock = await sessionLock.AcquireAsync(lease.SessionId, ct);
            var currentLease = await leases.GetBySessionAsync(lease.SessionId, ct);
            if (currentLease?.LeaseId != lease.LeaseId) continue;

            var pod = await cluster.GetAsync(_namespace, lease.SessionId, ct);
            if (lease.Phase == BrowserPhase.Failed)
            {
                await cluster.DeleteAsync(_namespace, lease.SessionId, ct);
            }
            else if (pod is null)
            {
                await cluster.DeleteAsync(_namespace, lease.SessionId, ct);
                await leases.SetFailedAsync(lease.LeaseId, "startup_timeout", ct);
            }
            else if (pod.FailureCode is { } failure)
            {
                await cluster.DeleteAsync(_namespace, lease.SessionId, ct);
                await leases.SetFailedAsync(lease.LeaseId, failure, ct);
            }
            else if (lease.Phase == BrowserPhase.Pending && pod is { Ready: true, PodIp: not null })
            {
                await leases.SetRunningAsync(lease.LeaseId, pod.PodIp, ct);
            }
        }

        foreach (var sessionId in await cluster.ListSessionIdsAsync(_namespace, ct))
        {
            await using var distributedLock = await sessionLock.AcquireAsync(sessionId, ct);
            var currentLease = await leases.GetBySessionAsync(sessionId, ct);
            var currentSession = await sessions.GetByIdAsync(sessionId, ct);
            if (currentLease is null || currentSession is null ||
                currentSession.Status is not (SessionStatus.Pending or SessionStatus.Running))
                await cluster.DeleteAsync(_namespace, sessionId, ct);
        }
    }
}