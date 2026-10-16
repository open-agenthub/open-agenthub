using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>
/// Deletes sessions whose self-deletion deadline has passed (docs/session-expiry.md). Runs in
/// the backend rather than as a Kubernetes TTL because only the backend knows the deadline's
/// basis and owns everything a session consists of besides its pod.
/// </summary>
public sealed class SessionExpirySweepService(
    ISessionStore store,
    ISessionService sessions,
    ISessionExpiryLock locks,
    IEnumerable<INotifier> notifiers,
    ILogger<SessionExpirySweepService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Per pass. A backlog larger than this (a long outage) is drained over several passes
    /// rather than in one, so a single failure cannot stall hundreds of deletions.
    /// </summary>
    public const int BatchSize = 50;

    public const string NotifierEvent = "session-expired";

    public async Task<int> SweepOnceAsync(DateTime now, CancellationToken ct)
    {
        var deleted = 0;
        foreach (var candidate in await store.ListExpiredAsync(now, BatchSize, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await TryExpireAsync(candidate, now, ct)) deleted++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // The row is still there; the next pass lists it again.
                logger.LogWarning(exception, "Expiring session {SessionId} failed; retrying next pass", candidate.Id);
            }
        }
        return deleted;
    }

    private async Task<bool> TryExpireAsync(SessionRecord candidate, DateTime now, CancellationToken ct)
    {
        await using var held = await locks.TryAcquireAsync(candidate.Id, ct);
        // Another replica is on it.
        if (held is null) return false;

        // Re-read under the lock: between the listing and here a touch, an edit that switched
        // the deadline off, or the other replica's deletion may have landed.
        var current = await store.GetByIdAsync(candidate.Id, ct);
        if (current is null || !SessionExpiry.IsDue(current, now)) return false;

        await sessions.DeleteSessionAsync(current.Owner, current.Id, ct);
        logger.LogInformation("Deleted session {SessionId} of {Owner}: auto-delete after {Seconds}s from {Basis}",
            current.Id, current.Owner, current.AutoDeleteAfterSeconds, current.AutoDeleteFrom);

        var basis = current.AutoDeleteFrom == SessionExpiry.FromStart ? "its start" : "its last use";
        var message = $"The session was deleted automatically after {Describe(current.AutoDeleteAfterSeconds ?? 0)} since {basis}.";
        // Every notifier catches its own failures, as in InternalController.NotifyAllAsync.
        await Task.WhenAll(notifiers.Select(n => n.NotifyAsync(current, NotifierEvent, message, ct)));
        return true;
    }

    public static string Describe(int seconds) => seconds switch
    {
        >= 86400 when seconds % 86400 == 0 => $"{seconds / 86400} d",
        >= 3600 when seconds % 3600 == 0 => $"{seconds / 3600} h",
        _ => $"{seconds / 60} min"
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepOnceAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Session expiry sweep failed");
            }
        }
    }
}
