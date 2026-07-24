using AgentHub.Api.Persistence;

namespace AgentHub.Api.Browser;

public sealed class BrowserReconcileService(
    IConfiguration configuration,
    IBrowserLeaseStore leases,
    ISessionStore sessions,
    IBrowserClusterClient cluster,
    IBrowserService browsers,
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

    internal async Task ReconcileAsync(CancellationToken ct)
    {
        var currentLeases = await leases.ListAsync(ct);
        var bySession = currentLeases.ToDictionary(lease => lease.SessionId, StringComparer.Ordinal);
        foreach (var lease in currentLeases)
        {
            var session = await sessions.GetByIdAsync(lease.SessionId, ct);
            if (session is null)
            {
                await browsers.StopAsync(lease.SessionId, ct);
                continue;
            }

            var pod = await cluster.GetAsync(_namespace, lease.SessionId, ct);
            if (pod is null && lease.Phase is BrowserPhase.Pending or BrowserPhase.Running)
                await leases.SetFailedAsync(lease.LeaseId, "startup_timeout", ct);
            else if (pod?.FailureCode is { } failure)
                await leases.SetFailedAsync(lease.LeaseId, failure, ct);
        }

        foreach (var sessionId in await cluster.ListSessionIdsAsync(_namespace, ct))
        {
            if (!bySession.ContainsKey(sessionId) ||
                await sessions.GetByIdAsync(sessionId, ct) is null)
                await cluster.DeleteAsync(_namespace, sessionId, ct);
        }
    }
}