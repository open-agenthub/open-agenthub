namespace AgentHub.Api.Ee.Slack;

/// <summary>Expired destinations must disappear even when the relay or license is disabled.</summary>
public sealed class SlackThreadPruneService(SlackThreadStore threads, ILogger<SlackThreadPruneService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try { await threads.PruneDeletedAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Pruning deleted thread destinations failed"); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
