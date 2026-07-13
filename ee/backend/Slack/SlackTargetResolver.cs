// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Collections.Concurrent;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Ee.Slack;

/// <summary>
/// Pure decision: which Slack conversation a session owner's notifications go to,
/// given their directory record and the configured fallback channel. No I/O — the
/// email→DM lookup is signalled via <c>lookupEmail</c> and performed by the resolver.
/// </summary>
public static class SlackTarget
{
    public static (string? channel, bool lookupEmail) Decide(AppUser? user, string? fallbackChannel)
    {
        if (user is { SlackEnabled: false }) return (null, false);                 // user opted out
        if (!string.IsNullOrWhiteSpace(user?.SlackChannelOverride))                // explicit override
            return (user!.SlackChannelOverride, false);
        if (!string.IsNullOrWhiteSpace(user?.Email)) return (null, true);          // resolve via email → DM
        return (string.IsNullOrWhiteSpace(fallbackChannel) ? null : fallbackChannel, false);
    }
}

/// <summary>Resolves the per-owner Slack conversation id (DM), caching lookups.</summary>
public interface ISlackTargetResolver
{
    Task<string?> ResolveAsync(string owner, CancellationToken ct = default);
    void Invalidate(string owner);
}

public sealed class SlackTargetResolver : ISlackTargetResolver
{
    private readonly UserDirectory _dir;
    private readonly SlackClient _slack;
    private readonly SlackOptions _opts;
    private readonly ConcurrentDictionary<string, string> _cache = new(); // owner -> channel id

    public SlackTargetResolver(UserDirectory dir, SlackClient slack, SlackOptions opts)
    { _dir = dir; _slack = slack; _opts = opts; }

    public async Task<string?> ResolveAsync(string owner, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(owner, out var cached)) return cached;

        var user = await _dir.GetAsync(owner, ct);
        var (channel, lookupEmail) = SlackTarget.Decide(user, _opts.Channel);

        if (lookupEmail && user?.Email is { } email)
            channel = await _slack.OpenImByEmailAsync(email, ct);

        if (channel is not null) _cache[owner] = channel;
        return channel;
    }

    public void Invalidate(string owner) => _cache.TryRemove(owner, out _);
}
