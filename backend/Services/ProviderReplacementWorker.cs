using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

public sealed class ProviderReplacementWorker(IProviderSwitchStore journal, ISessionService sessions,
    ILogger<ProviderReplacementWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (sessions is not KubernetesSessionService service) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var pending in await journal.PendingAsync(stoppingToken))
                    await service.ProcessProviderSwitchAsync(pending.Owner, pending.SessionId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogWarning(error, "Provider replacement polling failed; durable progress is retained."); }
            if (!await timer.WaitForNextTickAsync(stoppingToken)) return;
        }
    }
}
