using AgentHub.Api.Admin;
using AgentHub.Api.Chat;
using AgentHub.Api.Mcp;
using AgentHub.Api.Persistence;
using System.Diagnostics;
using AgentHub.Api.Browser;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace AgentHub.Api.Tests;

public class PostgresDataSourcesTests(ITestOutputHelper output)
{
    private static IConfiguration Config(string connectionString, string? maxPoolSize = null, string? lockPoolSize = null)
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString };
        if (maxPoolSize is not null) values[PostgresDataSources.MaxPoolSizeKey] = maxPoolSize;
        if (lockPoolSize is not null) values[PostgresDataSources.LockPoolSizeKey] = lockPoolSize;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static NpgsqlConnectionStringBuilder Built(IConfiguration configuration) =>
        new(PostgresDataSources.ConnectionString(configuration, "open-agenthub"));

    [Fact]
    public void PoolSize_DefaultsBelowNpgsqls100()
    {
        var built = Built(Config("Host=db.example.com;Database=agenthub"));

        Assert.Equal(PostgresDataSources.DefaultMaxPoolSize, built.MaxPoolSize);
        Assert.Equal("open-agenthub", built.ApplicationName);
        Assert.Equal("db.example.com", built.Host);
    }

    [Theory]
    [InlineData("Maximum Pool Size=7")]
    [InlineData("maximum pool size=7")]
    [InlineData("MaxPoolSize=7")]
    public void PoolSize_FromConnectionStringIsKept(string setting)
    {
        Assert.Equal(7, Built(Config($"Host=db.example.com;{setting}")).MaxPoolSize);
    }

    [Fact]
    public void PoolSize_ConfigurationWinsOverConnectionString()
    {
        Assert.Equal(3, Built(Config("Host=db.example.com;Maximum Pool Size=7", "3")).MaxPoolSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("many")]
    public void PoolSize_InvalidConfigurationFailsAtStartup(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => PostgresDataSources.ConnectionString(Config("Host=db.example.com", value), "open-agenthub"));
        Assert.Contains(PostgresDataSources.MaxPoolSizeKey, error.Message);
    }

    [Fact]
    public void PoolSize_DefaultRisesToAMinimumFromConnectionString()
    {
        Assert.Equal(30, Built(Config("Host=db.example.com;Minimum Pool Size=30")).MaxPoolSize);
    }

    [Fact]
    public void PoolSize_ConfiguredBelowMinimumFailsAtStartup()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => PostgresDataSources.ConnectionString(Config("Host=db.example.com;Minimum Pool Size=30", "10"), "open-agenthub"));
        Assert.Contains("Minimum Pool Size", error.Message);
    }

    [Fact]
    public void ApplicationName_FromConnectionStringIsKept()
    {
        Assert.Equal("mine", Built(Config("Host=db.example.com;Application Name=mine")).ApplicationName);
    }

    [Fact]
    public void LockPool_IsSmallAndSizedOnItsOwn()
    {
        var defaults = new NpgsqlConnectionStringBuilder(PostgresDataSources.LockConnectionString(
            Config("Host=db.example.com;Maximum Pool Size=50;Minimum Pool Size=10", maxPoolSize: "40")));
        Assert.Equal(PostgresDataSources.DefaultLockPoolSize, defaults.MaxPoolSize);
        Assert.Equal(0, defaults.MinPoolSize);
        Assert.Equal("open-agenthub-locks", defaults.ApplicationName);

        var sized = new NpgsqlConnectionStringBuilder(PostgresDataSources.LockConnectionString(
            Config("Host=db.example.com", lockPoolSize: "8")));
        Assert.Equal(8, sized.MaxPoolSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("few")]
    public void LockPool_InvalidSizeFailsAtStartup(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => PostgresDataSources.LockConnectionString(Config("Host=db.example.com", lockPoolSize: value)));
        Assert.Contains(PostgresDataSources.LockPoolSizeKey, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(";Application Name=mine")]
    public void Stores_ShareOnePool_LocksGetTheirOwn(string extra)
    {
        var configuration = Config($"Host=db.example.com;Database=pool_{Guid.NewGuid():N}{extra}");

        var shared = PostgresDataSources.Shared(configuration);

        Assert.Same(shared, PostgresDataSources.Shared(Config(configuration.GetConnectionString("Postgres")!)));
        Assert.NotSame(shared, PostgresDataSources.Locks(configuration));
        Assert.Same(PostgresDataSources.Locks(configuration), PostgresDataSources.Locks(configuration));
    }

    [PostgreSqlFact]
    public async Task LockWaiters_FillingTheirPool_DoNotBlockTheHoldersQueries()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
        // Timeout bounds how long an exhausted pool makes a caller wait before it throws.
        var configuration = Config(new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            ApplicationName = "locks_" + Guid.NewGuid().ToString("N")[..12],
            Timeout = 3
        }.ConnectionString, maxPoolSize: "3", lockPoolSize: "3");
        var locks = new PostgresBrowserSessionLock(configuration);
        var session = Guid.NewGuid().ToString("N");

        var held = await locks.AcquireAsync(session);
        // Two more requests for the same session wait in pg_advisory_lock, each on a connection:
        // with the holder's, that is every connection a pool of 3 has.
        var waiters = new[] { locks.AcquireAsync(session), locks.AcquireAsync(session) };
        await Task.Delay(500);
        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));

        // The holder's own work still gets a connection; in one shared pool of 3 it would wait
        // for the waiters, who wait for the holder, until the timeout failed it.
        await using (var query = PostgresDataSources.Shared(configuration).CreateCommand("SELECT 1"))
            Assert.Equal(1, await query.ExecuteScalarAsync());

        await held.DisposeAsync();
        // Whichever waiter gets the lock next has to release it before the other can.
        var pending = waiters.ToList();
        while (pending.Count > 0)
        {
            var next = await Task.WhenAny(pending);
            pending.Remove(next);
            await (await next).DisposeAsync();
        }
    }

    /// <summary>
    /// The lock pool at its defaults under a peak: every lock connection held, more lock requests
    /// queued behind them, and a burst of queries at the same time. The queries must not notice.
    /// </summary>
    [PostgreSqlFact]
    public async Task LockPool_AtCapacity_StarvesNeitherQueriesNorQueuedLocks()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
        // A database of its own, so the pools keep their default names and pg_stat_activity
        // shows only this test's connections.
        var database = "pool_peak_" + Guid.NewGuid().ToString("N")[..12];
        await using var admin = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(baseConnectionString) { Pooling = false }.ConnectionString);
        await using (var create = admin.CreateCommand($"CREATE DATABASE {database}")) await create.ExecuteNonQueryAsync();
        try
        {
            var configuration = Config(new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = database }.ConnectionString);
            var locks = new PostgresBrowserSessionLock(configuration);
            var shared = PostgresDataSources.Shared(configuration);
            var lockPoolSize = PostgresDataSources.DefaultLockPoolSize;

            var sessions = Enumerable.Range(0, lockPoolSize).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
            var held = new List<IAsyncDisposable>();
            foreach (var session in sessions) held.Add(await locks.AcquireAsync(session));
            // Beyond the pool: a second request for a held session and three for new ones.
            var queued = new[] { locks.AcquireAsync(sessions[0]) }
                .Concat(Enumerable.Range(0, 3).Select(_ => locks.AcquireAsync(Guid.NewGuid().ToString("N"))))
                .ToArray();

            var peak = new Dictionary<string, long>();
            using var sampling = new CancellationTokenSource();
            var sampler = Task.Run(async () =>
            {
                while (!sampling.IsCancellationRequested)
                {
                    await using var count = admin.CreateCommand(
                        "SELECT application_name, count(*) FROM pg_stat_activity WHERE datname = @db GROUP BY 1");
                    count.Parameters.AddWithValue("db", database);
                    await using var reader = await count.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        peak[reader.GetString(0)] = Math.Max(peak.GetValueOrDefault(reader.GetString(0)), reader.GetInt64(1));
                    await Task.Delay(50);
                }
            });

            var latencies = new List<double>();
            var burst = Stopwatch.StartNew();
            await Parallel.ForEachAsync(Enumerable.Range(0, 1000), new ParallelOptions { MaxDegreeOfParallelism = 64 }, async (_, ct) =>
            {
                var watch = Stopwatch.StartNew();
                await using var query = shared.CreateCommand("SELECT pg_sleep(0.005)");
                await query.ExecuteNonQueryAsync(ct);
                lock (latencies) latencies.Add(watch.Elapsed.TotalMilliseconds);
            });
            burst.Stop();
            Assert.All(queued, request => Assert.False(request.IsCompleted));

            var release = Stopwatch.StartNew();
            foreach (var lease in held) await lease.DisposeAsync();
            var granted = new List<double>();
            foreach (var request in queued)
            {
                await (await request).DisposeAsync();
                granted.Add(release.Elapsed.TotalMilliseconds);
            }
            await sampling.CancelAsync();
            await sampler;

            latencies.Sort();
            output.WriteLine($"1000 queries, 64 in flight, during a full lock pool: {burst.ElapsedMilliseconds} ms, " +
                $"p50 {latencies[500]:0.0} ms, p99 {latencies[989]:0.0} ms, max {latencies[^1]:0.0} ms");
            output.WriteLine("peak connections: " + string.Join(", ", peak.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")));
            output.WriteLine($"queued lock requests granted {string.Join(", ", granted.Select(ms => $"{ms:0} ms"))} after release");

            Assert.True(peak.GetValueOrDefault("open-agenthub-locks") <= lockPoolSize);
            Assert.True(peak.GetValueOrDefault("open-agenthub") <= PostgresDataSources.DefaultMaxPoolSize);
            // Well inside Npgsql's 15 s acquire timeout: the queue drains as soon as locks are released.
            Assert.All(granted, ms => Assert.True(ms < 5000));
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlFact]
    public async Task Stores_InitializedOneAfterAnother_LeaveOneIdleConnection()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
        var schema = "pool_" + Guid.NewGuid().ToString("N")[..12];
        // A name of its own, so the count below sees this test's pool and nothing the parallel
        // suite has open.
        var connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schema,
            ApplicationName = schema
        }.ConnectionString;
        await using var admin = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(baseConnectionString) { Pooling = false }.ConnectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            var configuration = Config(connectionString);
            await new UserDirectory(configuration).InitializeAsync();
            await new ChatLinkCodeStore(configuration).InitializeAsync();
            await new ApiTokenStore(configuration).InitializeAsync();
            await new McpClientStore(configuration).InitializeAsync();
            await new UserRoleStore(configuration).InitializeAsync();
            await new PostgresSessionStore(configuration).InitializeAsync();

            await using var count = admin.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name");
            count.Parameters.AddWithValue("name", schema);
            // One pool per store held six here.
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
