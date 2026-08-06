using AgentHub.Api.Models;
using AgentHub.Api.Otel;
using AgentHub.Api.Persistence;
using AgentHub.Api.Usage;
using Xunit;

namespace AgentHub.Api.Tests;

public class UsageLimitServiceTests
{
    private sealed class FakeUsageStore : IUsageStore
    {
        public double MonthCost { get; set; }
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AddDeltaAsync(SessionUsageDelta delta, CancellationToken ct = default) => Task.FromResult(true);
        public Task AddResourceSampleAsync(string sessionId, string owner, SessionResourceSample sample,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SessionUsage>> ListByOwnerAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionUsage>>(Array.Empty<SessionUsage>());
        public Task<SessionUsage?> GetAsync(string owner, string sessionId, CancellationToken ct = default)
            => Task.FromResult<SessionUsage?>(null);
        public Task<UsageSummary> SummaryAsync(string owner, DateTime? from, DateTime? to, CancellationToken ct = default)
            => Task.FromResult(new UsageSummary { Owner = owner });
        public Task<double> MonthToDateApiCostAsync(string owner, CancellationToken ct = default)
            => Task.FromResult(MonthCost);
    }

    private sealed class FakePersonalLimit(double? limit) : IPersonalUsageLimitSource
    {
        public Task<double?> GetUsageLimitAsync(string owner, CancellationToken ct = default)
            => Task.FromResult(limit);
    }

    private sealed class FakeAdminProvider(UsageLimit? limit) : IAdminUsageLimitProvider
    {
        public Task<UsageLimit?> GetLimitAsync(string owner, CancellationToken ct = default)
            => Task.FromResult(limit);
    }

    private static UsageLimitService Build(double monthCost, double? personal, params UsageLimit?[] adminLimits)
        => new(new FakeUsageStore { MonthCost = monthCost }, new FakePersonalLimit(personal),
            adminLimits.Select(l => (IAdminUsageLimitProvider)new FakeAdminProvider(l)).ToArray());

    [Fact]
    public async Task NoLimits_MeansUnlimited()
    {
        var status = await Build(123, null).GetStatusAsync("u");
        Assert.Null(status.EffectiveLimitUsd);
        Assert.False(status.Blocked);
        Assert.Equal(123, status.MonthApiCostUsd);
    }

    [Fact]
    public async Task StrictestLimitWins_AdminBelowPersonal()
    {
        var status = await Build(0, personal: 50, new UsageLimit(20, "group")).GetStatusAsync("u");
        Assert.Equal(20, status.EffectiveLimitUsd);
        Assert.Equal("group", status.Source);
        Assert.Equal(50, status.PersonalLimitUsd);
    }

    [Fact]
    public async Task StrictestLimitWins_PersonalBelowAdmin()
    {
        var status = await Build(0, personal: 5, new UsageLimit(20, "global")).GetStatusAsync("u");
        Assert.Equal(5, status.EffectiveLimitUsd);
        Assert.Equal("personal", status.Source);
    }

    [Fact]
    public async Task Blocked_WhenSpendReachesLimit()
    {
        var status = await Build(10, personal: 10).GetStatusAsync("u");
        Assert.True(status.Blocked);
    }

    [Fact]
    public async Task EnsureCanStart_Throws_ForApiKeySession_OverLimit()
    {
        var svc = Build(10, personal: 10);
        await Assert.ThrowsAsync<UsageLimitExceededException>(() =>
            svc.EnsureCanStartAsync("u", AgentKind.Claude, AgentAuthMode.ApiKey, hasSubscriptionCredentials: false));
    }

    [Fact]
    public async Task EnsureCanStart_AllowsSubscriptionSessions_EvenOverLimit()
    {
        var svc = Build(10, personal: 10);
        await svc.EnsureCanStartAsync("u", AgentKind.Claude, AgentAuthMode.Subscription, hasSubscriptionCredentials: true);
    }

    [Fact]
    public async Task EnsureCanStart_Auto_UsesSubscriptionCredentialPresence()
    {
        var svc = Build(10, personal: 10);
        // Auto + stored subscription login -> not API-billed -> allowed.
        await svc.EnsureCanStartAsync("u", AgentKind.Claude, AgentAuthMode.Auto, hasSubscriptionCredentials: true);
        // Auto without a subscription login falls back to the API key -> blocked.
        await Assert.ThrowsAsync<UsageLimitExceededException>(() =>
            svc.EnsureCanStartAsync("u", AgentKind.Claude, AgentAuthMode.Auto, hasSubscriptionCredentials: false));
    }

    [Fact]
    public async Task EnsureCanStart_IgnoresNonClaudeAgents()
    {
        var svc = Build(999, personal: 1);
        await svc.EnsureCanStartAsync("u", AgentKind.Codex, AgentAuthMode.ApiKey, hasSubscriptionCredentials: false);
    }

    [Fact]
    public async Task EnsureCanStart_AllowsApiSessions_UnderLimit()
    {
        var svc = Build(9.99, personal: 10);
        await svc.EnsureCanStartAsync("u", AgentKind.Claude, AgentAuthMode.ApiKey, hasSubscriptionCredentials: false);
    }
}
