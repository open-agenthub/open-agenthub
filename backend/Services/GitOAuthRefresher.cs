using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AgentHub.Api.Services;

/// <summary>A running session pod and the connected providers its repositories name.</summary>
public sealed record GitSessionTarget(string SessionId, string Owner, IReadOnlyList<string> ProviderIds);

/// <summary>The running sessions as the git token refresher sees them.</summary>
public interface IGitSessionCredentials
{
    /// <summary>Running sessions that name at least one connected provider; all owners when
    /// <paramref name="owner"/> is null.</summary>
    Task<IReadOnlyList<GitSessionTarget>> ListRunningAsync(string? owner, CancellationToken ct);

    /// <summary>
    /// Rebuilds the session's credential store from what is stored now and installs it in the pod
    /// without a restart. False when the pod cannot take it: not running, or an image from before
    /// the route. Throws when the pod could not be reached.
    /// </summary>
    Task<bool> ReinstallAsync(string sessionId, CancellationToken ct);
}

/// <summary>
/// Keeps the connected-provider tokens of running sessions alive (docs/git-credentials.md,
/// "Renewing a connected provider's token", issue #289). A session used to get the token once, at
/// start; GitLab's access tokens live two hours, so every longer session ended up pushing with a
/// dead one. The hub now renews a grant before it expires — one renewal per owner and provider,
/// whichever replica gets the lock — and installs the result in every running session of that
/// owner that uses the provider.
/// </summary>
public sealed class GitOAuthRefresher
{
    private readonly IGitAuthService _git;
    private readonly IGitSessionCredentials _sessions;
    private readonly ILogger<GitOAuthRefresher> _log;
    private readonly Channel<(string Owner, string ProviderId)> _changes =
        Channel.CreateUnbounded<(string, string)>(new UnboundedChannelOptions { SingleReader = true });
    // Sessions whose pod could not be reached when a change was handed out. The provider may
    // already have revoked the token they hold, so they are retried on every pass, not at the next
    // renewal two hours later.
    private readonly ConcurrentDictionary<string, byte> _retry = new();

    public GitOAuthRefresher(IGitAuthService git, IGitTokenChanges changes, IGitSessionCredentials sessions,
        ILogger<GitOAuthRefresher> log)
    {
        _git = git;
        _sessions = sessions;
        _log = log;
        changes.TokensChanged += (owner, providerId) => _changes.Writer.TryWrite((owner, providerId));
    }

    public ChannelReader<(string Owner, string ProviderId)> Changes => _changes.Reader;

    /// <summary>
    /// One pass: renews every grant a running session uses that is close to expiry, and retries the
    /// sessions a change did not reach. Grants no session uses are left alone — a session start
    /// renews on demand, and GitLab's refresh tokens do not lapse from disuse. Returns how many
    /// grants were renewed.
    /// </summary>
    public async Task<int> RefreshDueAsync(CancellationToken ct)
    {
        var targets = await _sessions.ListRunningAsync(owner: null, ct);
        var renewed = 0;
        foreach (var (owner, providerId) in targets
                     .SelectMany(t => t.ProviderIds.Select(p => (t.Owner, ProviderId: p)))
                     .Distinct())
        {
            try
            {
                // Busy means another replica (or a session start) is renewing it right now; its
                // change event hands the result on.
                if (await _git.RefreshIfDueAsync(owner, providerId, waitForLock: false, ct) == GitRefreshOutcome.Refreshed)
                    renewed++;
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One owner's broken grant must not keep the others from being renewed.
                _log.LogWarning(error, "Renewing a {Provider} grant failed; retrying next pass", providerId);
            }
        }

        foreach (var sessionId in _retry.Keys.ToList())
        {
            _retry.TryRemove(sessionId, out _);
            await InstallAsync(sessionId, ct);
        }
        return renewed;
    }

    /// <summary>Installs the owner's current state of the provider in each of the owner's running
    /// sessions that use it. Returns how many took it.</summary>
    public async Task<int> DistributeAsync(string owner, string providerId, CancellationToken ct)
    {
        var targets = (await _sessions.ListRunningAsync(owner, ct))
            .Where(t => t.Owner == owner && t.ProviderIds.Contains(providerId))
            .ToList();
        var results = await Task.WhenAll(targets.Select(t => InstallAsync(t.SessionId, ct)));
        return results.Count(installed => installed);
    }

    private async Task<bool> InstallAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            if (await _sessions.ReinstallAsync(sessionId, ct)) return true;
            _log.LogInformation("Session {SessionId} cannot take renewed git credentials without a restart", sessionId);
            return false;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _retry[sessionId] = 0;
            _log.LogWarning("Handing renewed git credentials to session {SessionId} failed ({Reason}); retrying next pass",
                sessionId, error.GetType().Name);
            return false;
        }
    }
}

/// <summary>Runs <see cref="GitOAuthRefresher.RefreshDueAsync"/> every <see cref="GitOAuthRefreshOptions.Interval"/>
/// and hands every token change to the running sessions as it happens.</summary>
public sealed class GitOAuthRefreshService(GitOAuthRefresher refresher, GitOAuthRefreshOptions options,
    ILogger<GitOAuthRefreshService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Changes are handed on even with the periodic renewal off: a new sign-in or a disconnect
        // must reach running sessions either way.
        Task.WhenAll(HandOnChangesAsync(stoppingToken), options.Enabled ? RenewAsync(stoppingToken) : Task.CompletedTask);

    private async Task RenewAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            try
            {
                await refresher.RefreshDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger.LogWarning(error, "Renewing git provider tokens failed; retrying next pass");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private async Task HandOnChangesAsync(CancellationToken stoppingToken)
    {
        // WaitToReadAsync answers false when stopping rather than throwing, so shutdown ends the loop.
        while (await WaitToReadAsync(stoppingToken))
        {
            while (refresher.Changes.TryRead(out var change))
            {
                try
                {
                    await refresher.DistributeAsync(change.Owner, change.ProviderId, stoppingToken);
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(error, "Handing a {Provider} token change to running sessions failed", change.ProviderId);
                }
            }
        }
    }

    private async Task<bool> WaitToReadAsync(CancellationToken ct)
    {
        try { return await refresher.Changes.WaitToReadAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
