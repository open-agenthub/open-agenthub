using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>Thrown when creating a session would exceed the owner's running-session soft limit.
/// Controllers map this to HTTP 429.</summary>
public sealed class SessionLimitExceededException(string message) : InvalidOperationException(message);

/// <summary>
/// Soft-limits how many non-terminal sessions an owner may have concurrently.
/// Running set: Pending | Running | Paused | Scheduled (Succeeded/Failed do not count).
/// </summary>
public static class SessionSoftLimit
{
    public const int DefaultMax = 20;

    private static readonly HashSet<string> RunningStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Pending", "Running", "Paused", "Scheduled"
    };

    /// <summary>Require a positive max; invalid values fall back to <see cref="DefaultMax"/>.</summary>
    public static int NormalizeMax(int configured) => configured > 0 ? configured : DefaultMax;

    public static bool IsRunningStatus(string? status) =>
        status is not null && RunningStatuses.Contains(status);

    /// <summary>
    /// Throws <see cref="SessionLimitExceededException"/> when the owner already has
    /// <paramref name="maxRunning"/> or more sessions in the running set.
    /// </summary>
    public static async Task EnsureCanCreateAsync(ISessionStore store, string owner, int maxRunning,
        CancellationToken ct = default)
    {
        maxRunning = NormalizeMax(maxRunning);
        var sessions = await store.ListAsync(owner, ct);
        var count = sessions.Count(s => IsRunningStatus(s.Status));
        if (count >= maxRunning)
            throw new SessionLimitExceededException(
                $"Running session limit reached ({count} of {maxRunning}). " +
                "Stop or delete a session before creating another.");
    }
}
