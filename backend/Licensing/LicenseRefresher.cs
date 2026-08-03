namespace AgentHub.Api.Licensing;

/// <summary>
/// Re-reads the license token from the database on a short interval. The verified claims
/// are cached in-process (see <see cref="EnterpriseLicense"/>), so with multiple backend
/// replicas an activation, deactivation or heartbeat renewal handled by one replica must
/// reach the others — this poller bounds that staleness to the refresh interval.
/// </summary>
public sealed class LicenseRefresher(
    IEnterpriseLicense license, IConfiguration cfg, ILogger<LicenseRefresher> log)
    : BackgroundService
{
    private readonly TimeSpan _interval =
        TimeSpan.FromSeconds(Math.Max(5, cfg.GetValue("Ee:License:RefreshSeconds", 30)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            // A single-row read + offline signature check — cheap enough to poll.
            try { await license.ReloadAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Periodic license refresh failed.");
            }
        }
    }
}
