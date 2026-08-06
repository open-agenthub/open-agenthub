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
