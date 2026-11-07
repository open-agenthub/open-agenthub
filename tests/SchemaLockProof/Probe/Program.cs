// #538 probe: the real PostgresSessionStore.InitializeAsync, with the production data source,
// while another transaction holds `SELECT id FROM sessions FOR UPDATE`.
//   current  - the schema is what this checkout creates
//   outdated - one column dropped and the store's recorded script (if any) marked as older
//   unrecorded - current, but without the record of applied scripts: what a database left by a
//              release before #538 looks like to this one on its first start
// A reader runs `SELECT count(*) FROM sessions` every 100 ms on its own connection throughout;
// the writer is released after HOLD seconds (default 10). Prints one JSON line.
using System.Diagnostics;
using System.Text.Json;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

var scenario = args.FirstOrDefault() ?? "current";
var hold = TimeSpan.FromSeconds(double.Parse(Environment.GetEnvironmentVariable("HOLD") ?? "10"));
var baseCs = Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set AGENTHUB_TEST_POSTGRES_CONNECTION_STRING to a disposable local server.");
var admin = new NpgsqlConnectionStringBuilder(baseCs) { Database = "postgres", Pooling = false };
if (admin.Host is not ("127.0.0.1" or "localhost")) throw new InvalidOperationException("Use a disposable local server.");
var name = $"schema_lock_probe_{Guid.NewGuid():N}";
await Exec(admin.ConnectionString, $"CREATE DATABASE {name}");
try
{
    var cs = new NpgsqlConnectionStringBuilder(baseCs) { Database = name }.ConnectionString;
    var config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = cs }).Build();
    var store = new PostgresSessionStore(config);
    await store.InitializeAsync();
    await Exec(cs, "INSERT INTO sessions (id, owner, mode, callback_token, agent_session_id) VALUES ('s1', 'o', 'Interactive', 't', 'a')");
    if (scenario == "outdated")
        await Exec(cs, """
            ALTER TABLE sessions DROP COLUMN question_title;
            DO $$ BEGIN
              IF to_regclass('schema_scripts') IS NOT NULL THEN
                UPDATE schema_scripts SET sha256 = 'previous' WHERE name = 'PostgresSessionStore';
              END IF;
            END $$;
            """);

    if (scenario == "unrecorded")
        await Exec(cs, "DROP TABLE IF EXISTS schema_scripts");

    await using var writer = new NpgsqlConnection(cs);
    await writer.OpenAsync();
    var transaction = await writer.BeginTransactionAsync();
    await new NpgsqlCommand("SELECT id FROM sessions FOR UPDATE", writer, transaction).ExecuteNonQueryAsync();

    var clock = Stopwatch.StartNew();
    double? initSeconds = null;
    string initOutcome = "running";
    var init = Task.Run(async () =>
    {
        try { await store.InitializeAsync(); initOutcome = "ok"; }
        catch (Exception ex) { initOutcome = $"{ex.GetType().Name}: {ex.InnerException?.GetType().Name ?? ex.Message}"; }
        initSeconds = clock.Elapsed.TotalSeconds;
    });

    await using var reader = new NpgsqlConnection(cs);
    await reader.OpenAsync();
    await using var observer = new NpgsqlConnection(cs);
    await observer.OpenAsync();
    var reads = new List<double>();
    var queuedSeen = 0;
    var released = false;
    double? releasedAt = null;
    while (clock.Elapsed < TimeSpan.FromSeconds(60) && !(init.IsCompleted && released))
    {
        if (!released && clock.Elapsed >= hold)
        {
            await transaction.RollbackAsync();
            released = true;
            releasedAt = clock.Elapsed.TotalSeconds;
        }
        var waiting = await new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE relation = 'sessions'::regclass AND NOT granted", observer).ExecuteScalarAsync();
        if ((long)waiting! > 0) queuedSeen++;
        var read = Stopwatch.StartNew();
        await new NpgsqlCommand("SELECT count(*) FROM sessions", reader) { CommandTimeout = 60 }.ExecuteScalarAsync();
        reads.Add(read.Elapsed.TotalMilliseconds);
        await Task.Delay(100);
    }
    await init;
    var column = await new NpgsqlCommand(
        "SELECT count(*) FROM information_schema.columns WHERE table_name = 'sessions' AND column_name = 'question_title'", observer).ExecuteScalarAsync();
    reads.Sort();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        scenario,
        holdSeconds = hold.TotalSeconds,
        init = initOutcome,
        initSeconds = initSeconds is { } s ? Math.Round(s, 2) : (double?)null,
        writerReleasedAtSeconds = releasedAt is { } r ? Math.Round(r, 2) : (double?)null,
        readsWhileRunning = reads.Count,
        readMaxMs = Math.Round(reads[^1]),
        readP50Ms = Math.Round(reads[reads.Count / 2]),
        readsOver1s = reads.Count(x => x > 1000),
        samplesWithQueuedSessionsLock = queuedSeen,
        questionTitleColumnPresent = (long)column! == 1,
    }));
}
finally
{
    NpgsqlConnection.ClearAllPools();
    await Exec(admin.ConnectionString, $"DROP DATABASE {name} WITH (FORCE)");
}

static async Task Exec(string cs, string sql)
{
    await using var connection = new NpgsqlConnection(cs);
    await connection.OpenAsync();
    await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync();
}
