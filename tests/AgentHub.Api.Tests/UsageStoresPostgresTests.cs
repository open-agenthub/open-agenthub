using AgentHub.Api.Ee.Identity;
using AgentHub.Api.Ee.Usage;
using AgentHub.Api.Licensing;
using AgentHub.Api.Models;
using AgentHub.Api.Otel;
using AgentHub.Api.Persistence;
using AgentHub.Api.Usage;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Postgres-backed tests for the usage cost split (reported vs estimated), the monthly
/// rollup that feeds usage limits, the enterprise limit store and the user-group store.
/// Skipped unless AGENTHUB_TEST_POSTGRES_CONNECTION_STRING is set (same pattern as the
/// other *_PostgresTests).
/// </summary>
public class UsageStoresPostgresTests
{
    // ---------------------------------------------------------------- usage store

    [PostgreSqlFact]
    public async Task AddDelta_TracksReportedAndEstimatedCost_WithAuthModeSnapshot()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.AddSessionAsync("sess-sub", "alice", AgentAuthMode.Subscription);

        // The parser fills the aggregate counters AND the per-model buckets in lockstep.
        var delta = new SessionUsageDelta { SessionId = "sess-sub", CostUsd = 0, InputTokens = 1_000_000 };
        delta.ModelTokens["claude-opus-4-8"] = new ModelTokenUsage { InputTokens = 1_000_000 }; // $5
        await db.Usage.AddDeltaAsync(delta);
        await db.Usage.AddDeltaAsync(delta); // deltas accumulate

        var row = Assert.Single(await db.Usage.ListByOwnerAsync("alice"));
        Assert.Equal(2_000_000, row.InputTokens);
        Assert.Equal(0, row.CostUsd);
        Assert.Equal(10, row.EstimatedCostUsd, 6);
        Assert.Equal("Subscription", row.AuthMode);
        Assert.False(row.ApiBilled);
    }

    [PostgreSqlFact]
    public async Task Summary_SplitsApiCost_FromSubscriptionEstimate()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.AddSessionAsync("s-api", "alice", AgentAuthMode.ApiKey);
        await db.AddSessionAsync("s-sub", "alice", AgentAuthMode.Subscription);

        var apiDelta = new SessionUsageDelta { SessionId = "s-api", CostUsd = 1.25, InputTokens = 100_000 };
        apiDelta.ModelTokens["claude-opus-4-8"] = new ModelTokenUsage { InputTokens = 100_000 };
        await db.Usage.AddDeltaAsync(apiDelta);

        var subDelta = new SessionUsageDelta { SessionId = "s-sub", CostUsd = 0, InputTokens = 1_000_000 };
        subDelta.ModelTokens["claude-opus-4-8"] = new ModelTokenUsage { InputTokens = 1_000_000 }; // $5
        await db.Usage.AddDeltaAsync(subDelta);

        var summary = await db.Usage.SummaryAsync("alice", null, null);
        Assert.Equal(1.25, summary.CostUsd, 6);
        Assert.Equal(1.25, summary.ApiCostUsd, 6);
        Assert.Equal(5, summary.SubscriptionEstimatedCostUsd, 6); // only the subscription session
        Assert.Equal(5.5, summary.EstimatedCostUsd, 6);           // both sessions estimated
    }

    [PostgreSqlFact]
    public async Task SubscriptionSession_ReportingACost_IsNotApiSpend()
    {
        // Claude Code 2.1.287 reports claude_code.cost.usage from its own price table for a
        // subscription login just as for an API key. That figure must not become API spend.
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.AddSessionAsync("s-sub", "alice", AgentAuthMode.Subscription);
        await db.AddSessionAsync("s-api", "alice", AgentAuthMode.ApiKey);

        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "s-sub", CostUsd = 0.003255, InputTokens = 400, OutputTokens = 50 });
        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "s-api", CostUsd = 0.003255, InputTokens = 400, OutputTokens = 50 });

        var rows = (await db.Usage.ListByOwnerAsync("alice")).ToDictionary(r => r.SessionId);
        Assert.False(rows["s-sub"].ApiBilled);
        Assert.Equal("subscription", rows["s-sub"].Billing);
        Assert.Equal(0.003255, rows["s-sub"].CostUsd, 9); // what the CLI said, kept for reference
        Assert.Equal(0, rows["s-sub"].ApiCostUsd);
        Assert.True(rows["s-api"].ApiBilled);
        Assert.Equal(0.003255, rows["s-api"].ApiCostUsd, 9);

        var summary = await db.Usage.SummaryAsync("alice", null, null);
        Assert.Equal(0.003255, summary.CostUsd, 9);
        Assert.Equal(1, summary.ApiSessionCount);
        Assert.Equal(1, summary.SubscriptionSessionCount);
        Assert.Equal(450, summary.SubscriptionTokens);
        Assert.Equal(450, summary.ApiTokens);
        Assert.Equal(0.003255, await db.Usage.MonthToDateApiCostAsync("alice"), 9);
    }

    [PostgreSqlFact]
    public async Task AutoSession_FollowsThePodLabel_AndIsUnknownWithoutOne()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.AddSessionAsync("s-auto-api", "alice", AgentAuthMode.Auto);
        await db.AddSessionAsync("s-auto-tui", "alice", AgentAuthMode.Auto);

        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "s-auto-api", CostUsd = 1, Billing = "api" });
        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "s-auto-tui", CostUsd = 2, Billing = "unknown" });

        var rows = (await db.Usage.ListByOwnerAsync("alice")).ToDictionary(r => r.SessionId);
        Assert.Equal("api", rows["s-auto-api"].Billing);
        Assert.Equal("unknown", rows["s-auto-tui"].Billing);
        Assert.Equal(1, await db.Usage.MonthToDateApiCostAsync("alice"), 6);
    }

    [PostgreSqlFact]
    public async Task ReportedUsage_AddsOnlyTheDifference_AndStaysWithItsOwner()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.AddSessionAsync("s-codex", "alice", AgentAuthMode.Subscription);
        await db.AddSessionAsync("s-oc", "bob", AgentAuthMode.ApiKey);

        var first = new AgentUsageReport(400, 50, 600, 0, 20, 1, null);
        await db.Usage.AddReportedUsageAsync("s-codex", "alice", "codex", "subscription", first);
        await db.Usage.AddReportedUsageAsync("s-codex", "alice", "codex", "subscription", first); // repeated: adds nothing
        await db.Usage.AddReportedUsageAsync("s-codex", "alice", "codex", "subscription", first with { InputTokens = 800, Turns = 2 });
        // A fresh pod without the archived rollouts reports from zero again: counted in full.
        await db.Usage.AddReportedUsageAsync("s-codex", "alice", "codex", "subscription", new AgentUsageReport(10, 1, 0, 0, 0, 1, null));

        await db.Usage.AddReportedUsageAsync("s-oc", "bob", "opencode", "api", new AgentUsageReport(400, 50, 600, 0, 20, 1, 0.002));
        await db.Usage.AddReportedUsageAsync("s-oc", "bob", "opencode", "api", new AgentUsageReport(800, 100, 1200, 0, 40, 2, 0.005));

        var codex = Assert.Single(await db.Usage.ListByOwnerAsync("alice"));
        Assert.Equal(810, codex.InputTokens);
        Assert.Equal(51, codex.OutputTokens);
        Assert.Equal(600, codex.CacheReadTokens);
        Assert.Equal(20, codex.ReasoningTokens);
        Assert.Equal(3, codex.Turns);
        Assert.Equal("codex", codex.Source);
        Assert.Equal("subscription", codex.Billing);
        Assert.False(codex.CostReported);
        Assert.Equal(0, codex.EstimatedCostUsd); // the Claude price table says nothing about these tokens

        var opencode = Assert.Single(await db.Usage.ListByOwnerAsync("bob"));
        Assert.True(opencode.CostReported);
        Assert.Equal(0.005, opencode.ApiCostUsd, 9);
        Assert.Equal(0.005, await db.Usage.MonthToDateApiCostAsync("bob"), 9);
        Assert.Equal(0, await db.Usage.MonthToDateApiCostAsync("alice"), 9);

        // Owner scoping: neither owner sees the other's session.
        Assert.Null(await db.Usage.GetAsync("bob", "s-codex"));
        Assert.Null(await db.Usage.GetAsync("alice", "s-oc"));
        var alice = await db.Usage.SummaryAsync("alice", null, null);
        Assert.Equal(1, alice.SessionCount);
        Assert.Equal(3, alice.SubscriptionTurns);
        Assert.Equal(0, alice.CostUsd);
    }

    [PostgreSqlFact]
    public async Task Usage_IsScopedToTheOwner()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.AddSessionAsync("s-alice", "alice", AgentAuthMode.ApiKey);
        await db.AddSessionAsync("s-bob", "bob", AgentAuthMode.Subscription);
        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "s-alice", CostUsd = 1, InputTokens = 10 });
        // A payload naming another owner cannot move a session: the sessions registry decides.
        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "s-bob", UserId = "alice", CostUsd = 1, InputTokens = 20 });

        Assert.Equal("s-alice", Assert.Single(await db.Usage.ListByOwnerAsync("alice")).SessionId);
        Assert.Equal("s-bob", Assert.Single(await db.Usage.ListByOwnerAsync("bob")).SessionId);
        Assert.Null(await db.Usage.GetAsync("alice", "s-bob"));
        Assert.Null(await db.Usage.GetAsync("bob", "s-alice"));
        var alice = await db.Usage.SummaryAsync("alice", null, null);
        Assert.Equal(1, alice.SessionCount);
        Assert.Equal(10, alice.InputTokens);
        Assert.Equal(1, alice.CostUsd, 6);
        Assert.Equal(0, (await db.Usage.SummaryAsync("bob", null, null)).CostUsd, 6);
        Assert.Equal(0, await db.Usage.MonthToDateApiCostAsync("bob"), 6);
    }

    [PostgreSqlFact]
    public async Task Initialize_ClassifiesRowsFromBeforeTheSplit()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.AddSessionAsync("old-sub", "alice", AgentAuthMode.Subscription);
        await db.AddSessionAsync("old-api", "alice", AgentAuthMode.ApiKey);
        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "old-sub", CostUsd = 3, InputTokens = 10 });
        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "old-api", CostUsd = 2, InputTokens = 10 });
        await db.ExecuteAsync("UPDATE session_usage SET billing = NULL, api_cost_usd = 0, usage_source = NULL");

        await db.Usage.InitializeAsync();

        var rows = (await db.Usage.ListByOwnerAsync("alice")).ToDictionary(r => r.SessionId);
        Assert.Equal("subscription", rows["old-sub"].Billing);
        Assert.Equal(0, rows["old-sub"].ApiCostUsd);
        Assert.Equal("api", rows["old-api"].Billing);
        Assert.Equal(2, rows["old-api"].ApiCostUsd);
        Assert.Equal("claude-otel", rows["old-api"].Source);
    }

    [PostgreSqlFact]
    public async Task MonthToDateApiCost_ComesFromTheMonthlyRollup()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.AddSessionAsync("s-api", "alice", AgentAuthMode.ApiKey);

        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "s-api", CostUsd = 2 });
        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "s-api", CostUsd = 0.5 });

        Assert.Equal(2.5, await db.Usage.MonthToDateApiCostAsync("alice"), 6);
        Assert.Equal(0, await db.Usage.MonthToDateApiCostAsync("someone-else"), 6);
    }

    [PostgreSqlFact]
    public async Task AddResourceSample_AccumulatesDeltas_AndHandlesPodRestarts()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.AddSessionAsync("sess-res", "alice", AgentAuthMode.Subscription);

        // First pod: two snapshots — totals follow the cumulative counters.
        await db.Usage.AddResourceSampleAsync("sess-res", "alice",
            new SessionResourceSample(CpuSeconds: 10, MemoryBytes: 500, RxBytes: 1_000, TxBytes: 100));
        await db.Usage.AddResourceSampleAsync("sess-res", "alice",
            new SessionResourceSample(CpuSeconds: 25, MemoryBytes: 300, RxBytes: 4_000, TxBytes: 250));

        var row = Assert.Single(await db.Usage.ListByOwnerAsync("alice"));
        Assert.Equal(25, row.CpuSeconds, 6);
        Assert.Equal(300, row.MemoryBytes);   // gauge: latest
        Assert.Equal(500, row.PeakMemoryBytes);
        Assert.Equal(4_000, row.RxBytes);
        Assert.Equal(250, row.TxBytes);

        // Resume = fresh pod, counters restart from zero: the full value is the delta.
        await db.Usage.AddResourceSampleAsync("sess-res", "alice",
            new SessionResourceSample(CpuSeconds: 5, MemoryBytes: 800, RxBytes: 500, TxBytes: 50));

        row = Assert.Single(await db.Usage.ListByOwnerAsync("alice"));
        Assert.Equal(30, row.CpuSeconds, 6);
        Assert.Equal(800, row.MemoryBytes);
        Assert.Equal(800, row.PeakMemoryBytes);
        Assert.Equal(4_500, row.RxBytes);
        Assert.Equal(300, row.TxBytes);

        // Resource samples and token deltas share the row without clobbering each other.
        await db.Usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "sess-res", InputTokens = 42 });
        row = Assert.Single(await db.Usage.ListByOwnerAsync("alice"));
        Assert.Equal(42, row.InputTokens);
        Assert.Equal(30, row.CpuSeconds, 6);

        var summary = await db.Usage.SummaryAsync("alice", null, null);
        Assert.Equal(30, summary.CpuSeconds, 6);
        Assert.Equal(4_500, summary.RxBytes);
        Assert.Equal(300, summary.TxBytes);
    }

    // ---------------------------------------------------------------- group store

    [PostgreSqlFact]
    public async Task Groups_ReplaceAndRoles_DriveAdminChecks()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.Groups.ReplaceGroupsAsync("alice", new[] { "devs", "leads" });
        await db.Groups.ReplaceGroupsAsync("bob", new[] { "devs" });

        Assert.Equal(new[] { "devs", "leads" }, await db.Groups.GetGroupsAsync("alice"));
        Assert.False(await db.Groups.AnyAdminGroupAsync());
        Assert.False(await db.Groups.IsInAdminGroupAsync("alice"));

        await db.Groups.SetGroupRoleAsync("leads", UserGroupStore.AdminRole);
        Assert.True(await db.Groups.AnyAdminGroupAsync());
        Assert.True(await db.Groups.IsInAdminGroupAsync("alice"));
        Assert.False(await db.Groups.IsInAdminGroupAsync("bob"));

        // Membership refresh from a new token drops the group -> admin right goes with it.
        await db.Groups.ReplaceGroupsAsync("alice", new[] { "devs" });
        Assert.False(await db.Groups.IsInAdminGroupAsync("alice"));

        var groups = await db.Groups.ListGroupsAsync();
        Assert.Equal(2, groups.Count); // devs (2 members), leads (role mapping, 0 members)
        Assert.Equal(2, groups.Single(g => g.Name == "devs").MemberCount);
        Assert.Equal(UserGroupStore.AdminRole, groups.Single(g => g.Name == "leads").Role);

        await db.Groups.SetGroupRoleAsync("leads", null); // clear mapping
        Assert.False(await db.Groups.AnyAdminGroupAsync());
    }

    // ---------------------------------------------------------------- limit store + provider

    [PostgreSqlFact]
    public async Task LimitStore_ResolvesStrictestApplicableLimit()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.Limits.SetAsync("global", "", 100);
        await db.Limits.SetAsync("group", "devs", 50);
        await db.Limits.SetAsync("user", "alice", 10);
        await db.Groups.ReplaceGroupsAsync("alice", new[] { "devs" });
        await db.Groups.ReplaceGroupsAsync("bob", new[] { "devs" });

        var provider = new EeUsageLimitProvider(new FakeLicense(true), db.Limits, db.Groups);
        Assert.Equal((10d, "user"), await ResolveAsync(provider, "alice"));
        Assert.Equal((50d, "group"), await ResolveAsync(provider, "bob"));
        Assert.Equal((100d, "global"), await ResolveAsync(provider, "carol"));

        await db.Limits.DeleteAsync("global", "");
        Assert.Null(await provider.GetLimitAsync("carol"));
    }

    [PostgreSqlFact]
    public async Task LimitProvider_ReturnsNothing_WithoutValidLicense()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        await db.Limits.SetAsync("global", "", 100);
        var provider = new EeUsageLimitProvider(new FakeLicense(false), db.Limits, db.Groups);
        Assert.Null(await provider.GetLimitAsync("alice"));
    }

    [PostgreSqlFact]
    public async Task UserDirectory_PersonalLimit_RoundTrips()
    {
        await using var db = await PostgresUsageDatabase.CreateAsync();
        Assert.Null(await db.Users.GetUsageLimitAsync("alice"));
        await db.Users.SetUsageLimitAsync("alice", 25);
        Assert.Equal(25, await db.Users.GetUsageLimitAsync("alice"));
        await db.Users.SetUsageLimitAsync("alice", null);
        Assert.Null(await db.Users.GetUsageLimitAsync("alice"));
    }

    private static async Task<(double, string)?> ResolveAsync(EeUsageLimitProvider provider, string owner)
        => await provider.GetLimitAsync(owner) is { } l ? (l.LimitUsd, l.Source) : null;

    private sealed class FakeLicense(bool enabled) : IEnterpriseLicense
    {
        public LicenseStatus Status => enabled
            ? new LicenseStatus { Valid = true }
            : new LicenseStatus { Valid = false, Reason = "test" };
        public bool Enabled => enabled;
        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}

internal sealed class PostgresUsageDatabase : IAsyncDisposable
{
    private readonly string _baseConnectionString;
    private readonly string _schema;

    private PostgresUsageDatabase(string baseConnectionString, string schema,
        PostgresUsageStore usage, PostgresSessionStore sessions, UserGroupStore groups,
        UsageLimitStore limits, UserDirectory users)
    {
        _baseConnectionString = baseConnectionString;
        _schema = schema;
        Usage = usage;
        Sessions = sessions;
        Groups = groups;
        Limits = limits;
        Users = users;
    }

    public PostgresUsageStore Usage { get; }
    public PostgresSessionStore Sessions { get; }
    public UserGroupStore Groups { get; }
    public UsageLimitStore Limits { get; }
    public UserDirectory Users { get; }

    public static async Task<PostgresUsageDatabase> CreateAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            throw new InvalidOperationException(
                $"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");
        }

        var schema = $"usage_test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(baseConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{schema}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = builder.ConnectionString
            })
            .Build();

        var sessions = new PostgresSessionStore(configuration);
        var usage = new PostgresUsageStore(configuration);
        var groups = new UserGroupStore(configuration);
        var limits = new UsageLimitStore(configuration);
        var users = new UserDirectory(configuration);

        try
        {
            await sessions.InitializeAsync(); // usage joins the sessions table
            await usage.InitializeAsync();
            await groups.InitializeAsync();
            await limits.InitializeAsync();
            await users.InitializeAsync();
            return new PostgresUsageDatabase(baseConnectionString, schema, usage, sessions, groups, limits, users);
        }
        catch
        {
            await DropSchemaAsync(baseConnectionString, schema);
            throw;
        }
    }

    public Task AddSessionAsync(string id, string owner, AgentAuthMode authMode)
        => Sessions.UpsertAsync(new SessionRecord
        {
            Id = id,
            Owner = owner,
            AuthMode = authMode,
            CallbackToken = $"token-{id}"
        });

    public async Task ExecuteAsync(string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString) { SearchPath = _schema };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
        => new(DropSchemaAsync(_baseConnectionString, _schema));

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE",
            connection);
        await command.ExecuteNonQueryAsync();
    }
}
