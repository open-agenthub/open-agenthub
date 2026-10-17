using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

public sealed class ClaudeLoginRefreshOptions
{
    /// <summary>
    /// How long before its access token expires a login is renewed. Far ahead of the CLI's own
    /// five minutes, so a session never reaches the point where it would refresh by itself: the
    /// hub then is the only party that spends refresh tokens.
    /// </summary>
    public TimeSpan Margin { get; set; } = TimeSpan.FromMinutes(60);

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A login the hub renewed this recently is left alone, so tokens that live shorter
    /// than <see cref="Margin"/> are renewed this often rather than on every pass.</summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromMinutes(15);

    public bool Enabled { get; set; } = true;
}

/// <summary>What the internal writeback endpoint asks after it stored a login a pod uploaded.</summary>
public interface IClaudeLoginSync
{
    /// <summary>
    /// Brings the other sessions on the account up to the stored login, or the uploading session,
    /// when its upload lost to a newer one. Best effort: failures are logged, never thrown.
    /// </summary>
    Task SyncAfterWritebackAsync(SessionRecord session, string accountId, byte[] uploaded, CancellationToken ct);
}

/// <summary>
/// The hub as the single owner of each stored Claude login (docs/provider-accounts.md, "The hub
/// owns each login"): it renews every account before its access token expires, whether or not a
/// session runs on it, and hands the result to every running session of that account. Sessions
/// sharing a login used to refresh it each on their own; the first refresh spent the refresh token
/// all the others held, and the next session started from a dead one (issue #116).
/// </summary>
public sealed class ClaudeLoginRefresher : IClaudeLoginSync
{
    private const int MaxWriteAttempts = 5;

    private readonly IProviderLoginRepository _logins;
    private readonly IClaudeTokenEndpoint _endpoint;
    private readonly IProviderLoginLock _locks;
    private readonly IAgentCredentialPusher _pusher;
    private readonly ClaudeLoginRefreshOptions _options;
    private readonly ILogger<ClaudeLoginRefresher> _log;

    public ClaudeLoginRefresher(IProviderLoginRepository logins, IClaudeTokenEndpoint endpoint, IProviderLoginLock locks,
        IAgentCredentialPusher pusher, ClaudeLoginRefreshOptions options, ILogger<ClaudeLoginRefresher> log)
    {
        _logins = logins;
        _endpoint = endpoint;
        _locks = locks;
        _pusher = pusher;
        _options = options;
        _log = log;
    }

    public bool IsDue(ProviderAccount account, byte[] file, DateTimeOffset now)
    {
        if (account.LoginRejectedAt is not null) return false;
        if (account.RefreshedAt is { } refreshed && now - new DateTimeOffset(DateTime.SpecifyKind(refreshed, DateTimeKind.Utc)) < _options.MinInterval)
            return false;
        return ClaudeCredentialFile.Read(file) is { IsBlank: false, RefreshToken: not null, ExpiresAt: { } expires }
            && expires - now < _options.Margin;
    }

    /// <summary>One pass over every stored Claude login. Returns how many were renewed.</summary>
    public async Task<int> RefreshDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var renewed = 0;
        foreach (var secret in await _logins.ListSecretNamesAsync(AgentKind.Claude, ct))
        {
            try
            {
                var snapshot = await _logins.ReadAsync(secret, AgentKind.Claude, ct);
                if (snapshot is null) continue;
                var due = snapshot.Accounts.Accounts
                    .Where(a => snapshot.Accounts.Files.TryGetValue(a.Id, out var file) && IsDue(a, file, now))
                    .Select(a => a.Id)
                    .ToList();
                foreach (var accountId in due)
                    if (await RefreshAccountAsync(secret, accountId, now, ct)) renewed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // One broken secret must not stop the others from being renewed.
                _log.LogWarning(error, "Renewing the Claude logins in {Secret} failed; retrying next pass", secret);
            }
        }
        return renewed;
    }

    public async Task<bool> RefreshAccountAsync(string secret, string accountId, DateTimeOffset now, CancellationToken ct)
    {
        await using var held = await _locks.TryAcquireAsync($"claude-login/{secret}/{accountId}", ct);
        // Another replica is renewing this login right now.
        if (held is null) return false;

        // Re-read under the lock: the other replica may have just renewed it, or a pod uploaded.
        var snapshot = await _logins.ReadAsync(secret, AgentKind.Claude, ct);
        var account = snapshot?.Accounts.Find(accountId);
        if (account is null || !snapshot!.Accounts.Files.TryGetValue(accountId, out var file) || !IsDue(account, file, now))
            return false;
        var state = ClaudeCredentialFile.Read(file)!;
        var spent = state.RefreshToken!;

        var result = await _endpoint.RefreshAsync(spent, state.Scopes, state.ClientId, ct);
        switch (result.Status)
        {
            case ClaudeRefreshStatus.Refreshed:
            {
                var renewed = ClaudeCredentialFile.WithRefreshedTokens(file, result.Tokens!, now);
                var stored = await ApplyIfUnchangedAsync(secret, accountId, spent, (set, current) =>
                {
                    set.Files[accountId] = renewed;
                    current.RefreshedAt = now.UtcDateTime;
                    current.LoginRejectedAt = null;
                }, ct);
                if (!stored)
                {
                    // The account got another login while the request was out (a sign-in in a
                    // session). That one is the user's choice and stays; these tokens are dropped.
                    _log.LogInformation("Renewed Claude login {Account} in {Secret} changed meanwhile; kept the newer one",
                        accountId, secret);
                    return false;
                }
                _log.LogInformation("Renewed Claude login {Account} in {Secret}", accountId, secret);
                await DistributeAsync(secret, accountId, renewed, exceptSessionId: null, ct);
                return true;
            }
            case ClaudeRefreshStatus.Rejected:
            {
                var marked = await ApplyIfUnchangedAsync(secret, accountId, spent,
                    (_, current) => current.LoginRejectedAt = now.UtcDateTime, ct);
                if (marked)
                    _log.LogWarning("Claude login {Account} in {Secret} was rejected by the provider ({Reason}); it needs a new sign-in",
                        accountId, secret, result.Detail);
                return false;
            }
            default:
                _log.LogWarning("Renewing Claude login {Account} in {Secret} failed ({Reason}); retrying next pass",
                    accountId, secret, result.Detail);
                return false;
        }
    }

    /// <summary>
    /// Applies a change to the account only while it still holds <paramref name="spentRefreshToken"/>,
    /// re-reading on a conflicting write. The same compare-and-swap the CLI does on its own file.
    /// </summary>
    private async Task<bool> ApplyIfUnchangedAsync(string secret, string accountId, string spentRefreshToken,
        Action<ProviderAccountSet, ProviderAccount> change, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxWriteAttempts; attempt++)
        {
            var snapshot = await _logins.ReadAsync(secret, AgentKind.Claude, ct);
            var account = snapshot?.Accounts.Find(accountId);
            if (account is null || !snapshot!.Accounts.Files.TryGetValue(accountId, out var current)) return false;
            if (ClaudeCredentialFile.Read(current)?.RefreshToken != spentRefreshToken) return false;
            change(snapshot.Accounts, account);
            snapshot.Accounts.Dirty = true;
            if (await _logins.TryWriteAsync(snapshot, AgentKind.Claude, ct)) return true;
        }
        return false;
    }

    /// <summary>Installs the file in every running session of the account. Returns how many took it.</summary>
    public async Task<int> DistributeAsync(string secret, string accountId, byte[] file, string? exceptSessionId,
        CancellationToken ct)
    {
        var sessions = (await _logins.ListRunningSessionsAsync(secret, AgentKind.Claude, accountId, ct))
            .Where(s => s.SessionId != exceptSessionId)
            .ToList();
        var results = await Task.WhenAll(sessions.Select(async session =>
        {
            try
            {
                if (await _pusher.RefreshAsync(session.PodIp, session.CallbackToken, AgentKind.Claude, file, ct)) return true;
                // An image from before the refresh route: it keeps its token until it refreshes
                // by itself, and its writeback then comes back through the newer-wins rule.
                _log.LogInformation("Session {SessionId} cannot take a renewed login without a restart; left as is",
                    session.SessionId);
                return false;
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _log.LogWarning("Handing the renewed Claude login to session {SessionId} failed: {Reason}",
                    session.SessionId, error.Message);
                return false;
            }
        }));
        return results.Count(installed => installed);
    }

    public async Task SyncAfterWritebackAsync(SessionRecord session, string accountId, byte[] uploaded, CancellationToken ct)
    {
        if (session.Agent != AgentKind.Claude) return;
        try
        {
            var secret = KubernetesSessionService.ProviderSecretName(session.Owner, AgentKind.Claude);
            var snapshot = await _logins.ReadAsync(secret, AgentKind.Claude, ct);
            if (snapshot is null || !snapshot.Accounts.Files.TryGetValue(accountId, out var stored)) return;
            if (stored.AsSpan().SequenceEqual(uploaded))
            {
                // The pod renewed the login itself (the hub was down, or the pod could not be
                // reached) and its tokens are now the account's: the others must not keep the
                // refresh token it just spent.
                await DistributeAsync(secret, accountId, stored, exceptSessionId: session.Id, ct);
                return;
            }
            // The upload lost to a newer login. The pod's own refresh token is spent; give it the
            // one that is not, before it tries to use its own.
            var self = (await _logins.ListRunningSessionsAsync(secret, AgentKind.Claude, accountId, ct))
                .FirstOrDefault(s => s.SessionId == session.Id);
            if (self is not null && !ClaudeCredentialFile.IsBlank(stored))
                await _pusher.RefreshAsync(self.PodIp, self.CallbackToken, AgentKind.Claude, stored, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(error, "Bringing the sessions of Claude account {Account} up to date failed", accountId);
        }
    }
}

/// <summary>Runs <see cref="ClaudeLoginRefresher.RefreshDueAsync"/> every few minutes.</summary>
public sealed class ClaudeLoginRefreshService(ClaudeLoginRefresher refresher, ClaudeLoginRefreshOptions options,
    ILogger<ClaudeLoginRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            try
            {
                await refresher.RefreshDueAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Renewing stored Claude logins failed; retrying next pass");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
