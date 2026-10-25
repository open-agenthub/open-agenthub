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
        public Task AddReportedUsageAsync(string sessionId, string owner, string source, string billing,
            AgentUsageReport report, CancellationToken ct = default) => Task.CompletedTask;
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

    // EnsureCanStartAsync takes the classification UsageBilling.ForPod makes for the start, the
    // same one usage reporting uses; the matrix itself is covered in UsageBillingTests.
    private static string Billing(AgentAuthMode auth, SessionMode mode, bool key, bool login)
        => UsageBilling.ForPod(auth, mode, key, login);

    [Fact]
    public async Task EnsureCanStart_Throws_ForApiKeySession_OverLimit()
    {
        var svc = Build(10, personal: 10);
        await Assert.ThrowsAsync<UsageLimitExceededException>(() =>
            svc.EnsureCanStartAsync("u", AgentKind.Claude, Billing(AgentAuthMode.ApiKey, SessionMode.Interactive, true, false)));
    }

    [Fact]
    public async Task EnsureCanStart_AllowsSubscriptionSessions_EvenOverLimit()
    {
        var svc = Build(10, personal: 10);
        await svc.EnsureCanStartAsync("u", AgentKind.Claude, Billing(AgentAuthMode.Subscription, SessionMode.Autonomous, false, true));
    }

    [Theory]
    // claude -p uses a stored key even with a login next to it: an unattended Auto start bills the
    // API and is refused over budget (on create, resume and restart alike — they share the gate).
    [InlineData(SessionMode.Autonomous, true, true, true)]
    [InlineData(SessionMode.Scheduled, true, false, true)]
    [InlineData(SessionMode.Autonomous, false, true, false)]
    // Interactive with both: the TUI asks the person, so it is unknown and never counts.
    [InlineData(SessionMode.Interactive, true, true, false)]
    // Interactive with only a key: the key is the one way the session can work.
    [InlineData(SessionMode.Interactive, true, false, true)]
    public async Task EnsureCanStart_Auto_FollowsTheReportingClassification(SessionMode mode, bool key, bool login, bool refused)
    {
        var svc = Build(10, personal: 10);
        var start = () => svc.EnsureCanStartAsync("u", AgentKind.Claude, Billing(AgentAuthMode.Auto, mode, key, login));
        if (refused) await Assert.ThrowsAsync<UsageLimitExceededException>(start);
        else await start();
    }

    [Fact]
    public async Task EnsureCanStart_IgnoresNonClaudeAgents()
    {
        var svc = Build(999, personal: 1);
        await svc.EnsureCanStartAsync("u", AgentKind.Codex, UsageBilling.Api);
    }

    [Fact]
    public async Task EnsureCanStart_AllowsApiSessions_UnderLimit()
    {
        var svc = Build(9.99, personal: 10);
        await svc.EnsureCanStartAsync("u", AgentKind.Claude, UsageBilling.Api);
    }
}
