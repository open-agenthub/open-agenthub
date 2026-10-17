using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>A provider secret as read; <see cref="ProviderAccountSet.ResourceVersion"/> is what a
/// conditional write must match.</summary>
public sealed record ProviderLoginSnapshot(string SecretName, ProviderAccountSet Accounts);

/// <summary>A running session pod whose agent uses one stored account.</summary>
public sealed record ProviderLoginSession(string SessionId, string PodIp, string CallbackToken);

/// <summary>
/// The stored provider logins as the background refresher sees them: by secret, because the
/// refresher walks every secret without knowing whose it is, and with conditional writes, because
/// a writeback from a pod or another backend replica may land between its read and its write.
/// </summary>
public interface IProviderLoginRepository
{
    Task<IReadOnlyList<string>> ListSecretNamesAsync(AgentKind agent, CancellationToken ct);

    Task<ProviderLoginSnapshot?> ReadAsync(string secretName, AgentKind agent, CancellationToken ct);

    /// <summary>False when the secret changed since <paramref name="snapshot"/> was read.</summary>
    Task<bool> TryWriteAsync(ProviderLoginSnapshot snapshot, AgentKind agent, CancellationToken ct);

    /// <summary>Running sessions of the secret's owner whose agent is on <paramref name="accountId"/>.</summary>
    Task<IReadOnlyList<ProviderLoginSession>> ListRunningSessionsAsync(string secretName, AgentKind agent,
        string accountId, CancellationToken ct);
}

/// <summary>Keeps two backend replicas from redeeming the same refresh token at once.</summary>
public interface IProviderLoginLock
{
    /// <summary>The lock, or null when another holder has it.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string key, CancellationToken ct = default);
}
