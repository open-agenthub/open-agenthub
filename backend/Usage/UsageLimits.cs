using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Usage;

/// <summary>A resolved usage limit and where it came from.</summary>
/// <param name="LimitUsd">Monthly API-cost ceiling in USD.</param>
/// <param name="Source">"personal" (self-set, CE) | "user" | "group" | "global" (admin-set, EE).</param>
public sealed record UsageLimit(double LimitUsd, string Source);

/// <summary>
/// Admin-configured limits (EE). The community edition has no provider registered beyond the
/// enterprise one, which itself returns null while no valid license is active — so CE behavior
/// is exactly "only the personal limit applies".
/// </summary>
public interface IAdminUsageLimitProvider
{
    /// <summary>The strictest admin limit applying to the owner, or null when none is set.</summary>
    Task<UsageLimit?> GetLimitAsync(string owner, CancellationToken ct = default);
}

/// <summary>Source of the user's self-set limit (implemented by <see cref="UserDirectory"/>).</summary>
public interface IPersonalUsageLimitSource
{
    Task<double?> GetUsageLimitAsync(string owner, CancellationToken ct = default);
}

/// <summary>Thrown when starting a session would exceed the owner's monthly API budget.
/// Derives from InvalidOperationException so existing controller mappings return 409.</summary>
public sealed class UsageLimitExceededException(string message) : InvalidOperationException(message);

/// <summary>Snapshot of the caller's limit situation for the usage page.</summary>
public sealed class UsageLimitStatus
{
    /// <summary>The self-set monthly limit (CE feature); null = none.</summary>
    public double? PersonalLimitUsd { get; init; }
    /// <summary>The strictest applicable limit (personal or admin-set); null = unlimited.</summary>
    public double? EffectiveLimitUsd { get; init; }
    /// <summary>Where the effective limit comes from: personal | user | group | global.</summary>
    public string? Source { get; init; }
    /// <summary>Real API spend this calendar month (UTC).</summary>
    public double MonthApiCostUsd { get; init; }
    /// <summary>True when new API-billed sessions are currently blocked.</summary>
    public bool Blocked => EffectiveLimitUsd is { } l && MonthApiCostUsd >= l;
}

/// <summary>
/// Resolves and enforces monthly API usage limits. The effective limit is the minimum of the
/// user's own limit (CE) and any admin-set limits (EE). Only API-billed Claude sessions are
/// gated — subscription sessions cost nothing extra and are never blocked.
/// </summary>
public sealed class UsageLimitService
{
    private readonly IUsageStore _usage;
    private readonly IPersonalUsageLimitSource _users;
    private readonly IEnumerable<IAdminUsageLimitProvider> _adminProviders;

    public UsageLimitService(IUsageStore usage, IPersonalUsageLimitSource users,
        IEnumerable<IAdminUsageLimitProvider> adminProviders)
    {
        _usage = usage;
        _users = users;
        _adminProviders = adminProviders;
    }

    public async Task<UsageLimitStatus> GetStatusAsync(string owner, CancellationToken ct = default)
    {
        var personal = await _users.GetUsageLimitAsync(owner, ct);
        var effective = personal is { } p ? new UsageLimit(p, "personal") : null;
        foreach (var provider in _adminProviders)
        {
            var admin = await provider.GetLimitAsync(owner, ct);
            if (admin is not null && (effective is null || admin.LimitUsd < effective.LimitUsd))
                effective = admin;
        }
        return new UsageLimitStatus
        {
            PersonalLimitUsd = personal,
            EffectiveLimitUsd = effective?.LimitUsd,
            Source = effective?.Source,
            MonthApiCostUsd = await _usage.MonthToDateApiCostAsync(owner, ct)
        };
    }

    /// <summary>
    /// Throws <see cref="UsageLimitExceededException"/> when the owner is over budget and the
    /// session would bill the Anthropic API. Subscription sessions pass; Auto passes when a
    /// Claude subscription login is stored (the key is only the fallback then). Non-Claude
    /// agents are not metered by the usage pipeline and therefore not gated.
    /// </summary>
    public async Task EnsureCanStartAsync(string owner, AgentKind agent, AgentAuthMode authMode,
        bool hasSubscriptionCredentials, CancellationToken ct = default)
    {
        if (agent != AgentKind.Claude) return;
        var apiBilled = authMode == AgentAuthMode.ApiKey ||
                        (authMode == AgentAuthMode.Auto && !hasSubscriptionCredentials);
        if (!apiBilled) return;

        var status = await GetStatusAsync(owner, ct);
        if (status.Blocked)
            throw new UsageLimitExceededException(
                $"Monthly API budget reached (${status.MonthApiCostUsd:0.00} of ${status.EffectiveLimitUsd:0.00}, " +
                $"{status.Source} limit). Use a Claude subscription session or raise the limit.");
    }
}
