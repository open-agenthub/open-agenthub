using System.Net;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;

namespace AgentHub.Api.Browser;

public sealed class KubernetesBrowserService : IBrowserService
{
    private static readonly TimeSpan StateUrlTtl = TimeSpan.FromMinutes(15);
    private readonly IBrowserLeaseStore _leases;
    private readonly ISessionStore _sessions;
    private readonly IArtifactStore _artifacts;
    private readonly IBrowserClusterClient _cluster;
    private readonly ILogger<KubernetesBrowserService> _log;
    private readonly IBrowserSessionLock _sessionLock;
    private readonly BrowserOptions _options;
    private readonly string _namespace;
    private readonly string _controlNamespace;
    private readonly string _callbackBaseUrl;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new();

    public KubernetesBrowserService(IConfiguration configuration, IBrowserLeaseStore leases,
        ISessionStore sessions, IArtifactStore artifacts, IBrowserClusterClient cluster,
        IBrowserSessionLock sessionLock, ILogger<KubernetesBrowserService> log)
    {
        _leases = leases;
        _sessions = sessions;
        _artifacts = artifacts;
        _cluster = cluster;
        _log = log;
        _sessionLock = sessionLock;
        _options = configuration.GetSection("Browser").Get<BrowserOptions>() ?? new BrowserOptions();
        _namespace = configuration["AgentHub:Namespace"] ?? "agenthub-sessions";
        _controlNamespace = configuration["AgentHub:ControlNamespace"] ?? "agenthub";
        _callbackBaseUrl = (configuration["AgentHub:CallbackBaseUrl"] ??
            "http://agenthub-backend.agenthub.svc.cluster.local").TrimEnd('/');
    }

    public async Task<BrowserConnection> EnsureAsync(
        SessionRecord session, IPAddress agentPodIp, CancellationToken ct = default)
    {
        if (!_options.Enabled)
            throw new InvalidOperationException("Integrated browsers are disabled.");
        if (session.Mode == Models.SessionMode.Scheduled)
            throw new InvalidOperationException("Scheduled sessions cannot start an interactive browser.");

        var gate = _sessionLocks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var distributedLock = await _sessionLock.AcquireAsync(session.Id, ct);
            var existing = await _leases.GetBySessionAsync(session.Id, ct);
            if (existing is { Phase: BrowserPhase.Running, PodIp: not null })
                return Connection(existing);
            if (existing?.Phase == BrowserPhase.Failed)
            {
                await _cluster.DeleteAsync(_namespace, session.Id, ct);
                await _leases.DeleteAsync(existing.LeaseId, ct);
                existing = null;
            }

            BrowserLease lease;
            if (existing is null)
            {
                var rawToken = RandomToken();
                lease = BrowserLease.Pending(session.Id, RandomId(), HashToken(rawToken));
                if (await _leases.TryCreateAsync(lease, ct))
                {
                    var resources = BrowserPodSpecFactory.Build(session, lease, new BrowserPodContext
                    {
                        Namespace = _namespace,
                        ControlNamespace = _controlNamespace,
                        CallbackUrl = $"{_callbackBaseUrl}/internal/browser-leases/{lease.LeaseId}/state",
                        LeaseToken = rawToken,
                        AgentPodIp = agentPodIp,
                        Options = _options
                    });
                    try
                    {
                        await _cluster.CreateAsync(resources, ct);
                    }
                    catch
                    {
                        await FailAndCleanAsync(lease, "startup_timeout", ct);
                        throw;
                    }
                }
                else
                {
                    lease = await _leases.GetBySessionAsync(session.Id, ct)
                        ?? throw new InvalidOperationException("Browser lease creation raced without a winner.");
                }
            }
            else
            {
                lease = existing;
            }

            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(_options.StartupTimeoutSeconds);
                while (DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    var pod = await _cluster.GetAsync(_namespace, session.Id, ct);
                    if (pod?.FailureCode is { } failure)
                    {
                        await FailAndCleanAsync(lease, failure, ct);
                        throw new InvalidOperationException($"Browser startup failed: {failure}.");
                    }
                    if (pod is { Ready: true, PodIp: not null })
                    {
                        await _leases.SetRunningAsync(lease.LeaseId, pod.PodIp, ct);
                        return Connection(lease with { Phase = BrowserPhase.Running, PodIp = pod.PodIp });
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                }

                await FailAndCleanAsync(lease, "startup_timeout", ct);
                throw new TimeoutException("Browser did not become ready before the startup timeout.");
            }
            catch (OperationCanceledException)
            {
                try { await _cluster.DeleteAsync(_namespace, lease.SessionId, CancellationToken.None); }
                finally { await _leases.DeleteAsync(lease.LeaseId, CancellationToken.None); }
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BrowserSummary> GetSummaryAsync(
        string sessionId, CancellationToken ct = default)
    {
        var lease = await _leases.GetBySessionAsync(sessionId, ct);
        return lease?.ToSummary(_options.ScreenWidth, _options.ScreenHeight)
            ?? new BrowserSummary(BrowserPhase.Stopped, _options.ScreenWidth, _options.ScreenHeight);
    }

    public async Task<IReadOnlyDictionary<string, BrowserSummary>> GetSummariesAsync(
        IReadOnlyCollection<string> sessionIds, CancellationToken ct = default)
    {
        var leases = await _leases.ListBySessionsAsync(sessionIds, ct);
        return sessionIds.Distinct(StringComparer.Ordinal).ToDictionary(
            id => id,
            id => leases.TryGetValue(id, out var lease)
                ? lease.ToSummary(_options.ScreenWidth, _options.ScreenHeight)
                : new BrowserSummary(BrowserPhase.Stopped, _options.ScreenWidth, _options.ScreenHeight),
            StringComparer.Ordinal);
    }

    public async Task<BrowserConnection?> GetConnectionAsync(
        string sessionId, CancellationToken ct = default)
    {
        var lease = await _leases.GetBySessionAsync(sessionId, ct);
        return lease is { Phase: BrowserPhase.Running, PodIp: not null }
            ? Connection(lease) : null;
    }

    public async Task StopAsync(string sessionId, CancellationToken ct = default)
    {
        var gate = _sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using var distributedLock = await _sessionLock.AcquireAsync(sessionId, ct);
            var lease = await _leases.GetBySessionAsync(sessionId, ct);
            if (lease is not null) await _leases.SetStoppingAsync(lease.LeaseId, ct);
            await _cluster.DeleteAsync(_namespace, sessionId, ct);
            if (lease is not null) await _leases.DeleteAsync(lease.LeaseId, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<BrowserStateUrls?> MintStateUrlsAsync(
        string leaseId, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 512) return null;
        var lease = await _leases.GetByLeaseAsync(leaseId, ct);
        if (lease is null) return null;
        var supplied = HashToken(token);
        if (supplied.Length != lease.TokenHash.Length ||
            !CryptographicOperations.FixedTimeEquals(supplied, lease.TokenHash))
            return null;

        var session = await _sessions.GetByIdAsync(lease.SessionId, ct);
        if (session is null) return null;
        var key = IArtifactStore.BrowserCookiesKey(SanitizeOwner(session.Owner), lease.SessionId);
        return new BrowserStateUrls(
            _artifacts.PresignGet(key, StateUrlTtl),
            _artifacts.PresignPut(key, StateUrlTtl));
    }

    public Task DeleteStateAsync(SessionRecord session, CancellationToken ct = default) =>
        _artifacts.DeleteAsync(IArtifactStore.BrowserCookiesKey(
            SanitizeOwner(session.Owner), session.Id), ct);

    private async Task FailAndCleanAsync(BrowserLease lease, string failure, CancellationToken ct)
    {
        try { await _cluster.DeleteAsync(_namespace, lease.SessionId, ct); }
        finally { await _leases.SetFailedAsync(lease.LeaseId, failure, ct); }
        _log.LogWarning("Browser startup for session {SessionId} failed with {Failure}",
            lease.SessionId, failure);
    }

    private BrowserConnection Connection(BrowserLease lease) => new(
        lease.ToSummary(_options.ScreenWidth, _options.ScreenHeight),
        $"http://{lease.PodIp}:9222", lease.PodIp!);

    private static byte[] HashToken(string token) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(token));
    private static string RandomToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string RandomId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private static string SanitizeOwner(string owner)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..16]
            .ToLowerInvariant();
        return $"u-{hash}";
    }
}