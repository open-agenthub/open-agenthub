namespace AgentHub.Api.Services;

public sealed class SessionRuntimeImageSweepService(ISessionService sessions,
    ILogger<SessionRuntimeImageSweepService> logger) : BackgroundService
{
    private void LogFailure(Exception exception) =>
        logger.LogWarning(exception, "Runtime image sweep failed; retrying next pass");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (sessions is KubernetesSessionService cluster)
                    await cluster.ReconcileRuntimeImagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (HttpRequestException exception) { LogFailure(exception); }
            catch (k8s.Autorest.HttpOperationException exception) { LogFailure(exception); }
            catch (Npgsql.NpgsqlException exception) { LogFailure(exception); }
        }
    }
}
