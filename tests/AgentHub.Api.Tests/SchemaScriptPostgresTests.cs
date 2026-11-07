using System.Diagnostics;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// #538: a session transaction holding row locks on <c>sessions</c> made a starting backend queue
/// its <c>ALTER TABLE</c> behind it, every later read of the table queue behind that, and the
/// start fail after the 30 s command timeout — even though the schema was already current. These
/// hold such a transaction against the real store initialisation.
/// </summary>
[Collection(nameof(SchemaScriptPostgresTests))]
public sealed class SchemaScriptPostgresTests
{
    [PostgreSqlFact]
    public async Task A_current_schema_starts_without_ddl_while_a_session_row_is_locked()
    {
        await using var db = await Database.CreateAsync();
        var store = new PostgresSessionStore(db.Configuration);
        await store.InitializeAsync();
        await db.ExecAsync("INSERT INTO sessions (id, owner, mode, callback_token, agent_session_id) VALUES ('s1', 'o', 'Interactive', 't', 'a')");

        await using var writer = await db.HoldAsync("SELECT id FROM sessions FOR UPDATE");
        var started = Stopwatch.StartNew();
        await store.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1), $"took {started.Elapsed}");
        Assert.Equal(1L, await db.CountAsync("SELECT count(*) FROM sessions", TimeSpan.FromSeconds(1)));
        Assert.Equal(0L, await db.CountAsync("SELECT count(*) FROM pg_locks WHERE NOT granted AND relation = 'sessions'::regclass"));
    }

    [PostgreSqlFact]
    public async Task An_outdated_schema_waits_in_short_attempts_that_let_reads_through()
    {
        await using var db = await Database.CreateAsync();
        var store = new PostgresSessionStore(db.Configuration);
        await store.InitializeAsync();
        // What the previous release left: a column fewer, and its own script recorded.
        await db.ExecAsync("""
            ALTER TABLE sessions DROP COLUMN question_title;
            UPDATE schema_scripts SET sha256 = 'previous' WHERE name = 'PostgresSessionStore';
            INSERT INTO sessions (id, owner, mode, callback_token, agent_session_id) VALUES ('s1', 'o', 'Interactive', 't', 'a');
            """);

        await using var writer = await db.HoldAsync("SELECT id FROM sessions FOR UPDATE");
        var init = store.InitializeAsync();
        var slowest = TimeSpan.Zero;
        var held = Stopwatch.StartNew();
        while (held.Elapsed < TimeSpan.FromSeconds(7))
        {
            var read = Stopwatch.StartNew();
            Assert.Equal(1L, await db.CountAsync("SELECT count(*) FROM sessions", TimeSpan.FromSeconds(10)));
            slowest = read.Elapsed > slowest ? read.Elapsed : slowest;
            await Task.Delay(100);
        }
        Assert.False(init.IsCompleted);
        await writer.DisposeAsync();
        await init.WaitAsync(TimeSpan.FromSeconds(30));

        // The default lock timeout is 2 s; a read can arrive just after an attempt has queued.
        Assert.True(slowest < TimeSpan.FromSeconds(3), $"a read waited {slowest}");
        Assert.Equal(1L, await db.CountAsync(
            "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'sessions' AND column_name = 'question_title'"));
    }

    [PostgreSqlFact]
    public async Task A_lock_that_is_never_released_ends_in_a_clear_error_and_nothing_half_applied()
    {
        await using var db = await Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE t (id INT)");
        await using var writer = await db.HoldAsync("SELECT * FROM t FOR UPDATE");
        var options = new SchemaScriptOptions(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1.5));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => SchemaScript.ApplyAsync(db.DataSource, "t",
            "CREATE TABLE u (id INT); ALTER TABLE t ADD COLUMN IF NOT EXISTS name TEXT;", options: options));

        Assert.Contains("Schema script t could not lock its tables", error.Message);
        Assert.Contains("idle in transaction", error.Message);
        // Neither the script's table nor its record: the attempts were rolled back whole.
        Assert.Equal(0L, await db.CountAsync(
            "SELECT count(*) FROM pg_tables WHERE schemaname = current_schema() AND tablename IN ('u', 'schema_scripts')"));
    }

    [PostgreSqlFact]
    public async Task A_changed_script_runs_again_and_an_unchanged_one_does_not()
    {
        await using var db = await Database.CreateAsync();
        await SchemaScript.ApplyAsync(db.DataSource, "t", "CREATE TABLE IF NOT EXISTS t (id INT);");
        await SchemaScript.ApplyAsync(db.DataSource, "t", "CREATE TABLE IF NOT EXISTS t (id INT); ALTER TABLE t ADD COLUMN IF NOT EXISTS name TEXT;");
        Assert.Equal(1L, await db.CountAsync("SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 't' AND column_name = 'name'"));

        // ACCESS EXCLUSIVE blocks even a catalogue-only ALTER; the unchanged script must not need it.
        await using var writer = await db.HoldAsync("LOCK TABLE t IN ACCESS EXCLUSIVE MODE");
        await SchemaScript.ApplyAsync(db.DataSource, "t",
            "CREATE TABLE IF NOT EXISTS t (id INT); ALTER TABLE t ADD COLUMN IF NOT EXISTS name TEXT;").WaitAsync(TimeSpan.FromSeconds(1));
    }

    internal sealed class Database : IAsyncDisposable
    {
        private readonly string _base;
        private readonly string _name;
        private readonly string _connectionString;
        public IConfiguration Configuration { get; }
        public NpgsqlDataSource DataSource => PostgresDataSources.Shared(Configuration);

        private Database(string baseConnectionString, string name)
        {
            _base = baseConnectionString;
            _name = name;
            // Unpooled: a pooled idle connection per test would add up against CI's max_connections.
            _connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = name, Pooling = false }.ConnectionString;
            Configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = _connectionString })
                .Build();
        }

        public static async Task<Database> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
            var db = new Database(connectionString, $"schema_script_{Guid.NewGuid():N}");
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA \"{db._name}\"", connection);
            await command.ExecuteNonQueryAsync();
            return db;
        }

        public async Task ExecAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> CountAsync(string sql, TimeSpan? timeout = null)
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync().WaitAsync(timeout ?? TimeSpan.FromSeconds(30)))!;
        }

        /// <summary>Runs <paramref name="sql"/> in a transaction that stays open until disposed.</summary>
        public async Task<IAsyncDisposable> HoldAsync(string sql)
        {
            var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
                await command.ExecuteNonQueryAsync();
            return new Held(connection, transaction);
        }

        private sealed class Held(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
        {
            private bool _disposed;
            public async ValueTask DisposeAsync()
            {
                if (_disposed) return;
                _disposed = true;
                await transaction.RollbackAsync();
                await connection.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_base);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_name}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}

/// <summary>
/// The script retries share <see cref="SchemaMigrationLock"/>'s budget, so the whole startup ends
/// with the clear error before the chart's startup probe (630 s) kills the replica. In the
/// startup collection because it takes the one schema migration lock.
/// </summary>
[Collection(nameof(StartupConcurrencyPostgresTests))]
public sealed class SchemaScriptDeadlinePostgresTests
{
    [PostgreSqlFact]
    public async Task Script_retries_stop_at_the_migration_lock_budget()
    {
        await using var db = await SchemaScriptPostgresTests.Database.CreateAsync();
        await db.ExecAsync("CREATE TABLE t (id INT)");
        await using var writer = await db.HoldAsync("SELECT * FROM t FOR UPDATE");

        var started = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => SchemaMigrationLock.RunAsync(db.Configuration,
            NullLogger.Instance, () => SchemaScript.ApplyAsync(db.DataSource, "t", "ALTER TABLE t ADD COLUMN IF NOT EXISTS name TEXT;"),
            timeout: TimeSpan.FromSeconds(6)));

        Assert.StartsWith("Schema script t could not lock its tables", error.Message);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
    }
}
