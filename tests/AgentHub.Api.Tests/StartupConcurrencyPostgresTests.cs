using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The quickstart starts two backend replicas at once against one database (#371). These run the
/// real backend twice, as the cluster does, because what broke is only visible in the real process:
/// a dependency cycle that deadlocked the host's startup, and two migrations racing each other.
/// </summary>
public sealed class StartupConcurrencyPostgresTests
{
    private static readonly TimeSpan HealthyWithin = TimeSpan.FromSeconds(60);

    [PostgreSqlTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_replicas_starting_together_on_a_fresh_database_both_become_healthy(bool chatEnabled)
    {
        await using var db = await Schema.CreateAsync();

        await StartTogetherAsync(db, replicas: 2, chatEnabled);

        Assert.Equal(4, await db.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE connamespace = current_schema()::regnamespace AND conname LIKE '%_session_fk'"));
    }

    [PostgreSqlFact]
    public async Task Two_replicas_starting_together_on_a_1_7_0_database_both_migrate_it()
    {
        await using var db = await Schema.CreateAsync();
        await StartTogetherAsync(db, replicas: 1);
        // Back to what 1.7.0 left: no session foreign keys, no deleted-session markers, and metadata
        // of sessions that were deleted. #367/#368 prune those rows and add the constraints.
        await db.ExecAsync("""
            ALTER TABLE session_shares DROP CONSTRAINT session_shares_session_fk;
            ALTER TABLE session_share_links DROP CONSTRAINT session_share_links_session_fk;
            ALTER TABLE session_mcp_policies DROP CONSTRAINT session_mcp_policies_session_fk;
            ALTER TABLE slack_threads DROP CONSTRAINT slack_threads_session_fk;
            DROP TRIGGER archive_deleted_slack_thread ON slack_threads;
            DROP FUNCTION archive_deleted_slack_thread();
            DROP TABLE chat_deleted_sessions;
            ALTER TABLE sessions DROP COLUMN question_open, DROP COLUMN question_title;
            INSERT INTO sessions (id, owner, mode, callback_token, agent_session_id)
            VALUES ('live0001', 'admin', 'Interactive', 'cb-live', 'a1');
            INSERT INTO session_shares (session_id, recipient_owner, role)
            VALUES ('gone0001', 'bob', 'Viewer'), ('live0001', 'bob', 'Viewer');
            INSERT INTO session_share_links (id, session_id, token_hash, role) VALUES ('l1', 'gone0001', '\x01', 'Viewer');
            INSERT INTO session_mcp_policies (session_id, blocked_servers, blocked_tools) VALUES ('gone0001', '[]', '[]');
            INSERT INTO slack_threads (session_id, owner, channel, thread_ts) VALUES ('gone0001', 'admin', 'C1', '1.1');
            """);

        await StartTogetherAsync(db, replicas: 2, chatEnabled: true);

        Assert.Equal(4, await db.CountAsync(
            "SELECT count(*) FROM pg_constraint WHERE connamespace = current_schema()::regnamespace AND conname LIKE '%_session_fk'"));
        Assert.Equal(1, await db.CountAsync("SELECT count(*) FROM session_shares"));
        Assert.Equal(0, await db.CountAsync(
            "SELECT (SELECT count(*) FROM session_share_links) + (SELECT count(*) FROM session_mcp_policies) + (SELECT count(*) FROM slack_threads)"));
    }

    [PostgreSqlFact]
    public async Task The_lock_admits_one_migration_at_a_time()
    {
        var config = Schema.BaseConfig();
        var inside = 0;
        var most = 0;
        async Task Migrate()
        {
            most = Math.Max(most, Interlocked.Increment(ref inside));
            await Task.Delay(300);
            Interlocked.Decrement(ref inside);
        }

        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            SchemaMigrationLock.RunAsync(config, NullLogger.Instance, Migrate)));

        Assert.Equal(1, most);
    }

    [PostgreSqlFact]
    public async Task A_failed_migration_releases_the_lock()
    {
        var config = Schema.BaseConfig();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrationLock.RunAsync(
            config, NullLogger.Instance, () => throw new InvalidOperationException("migration failed")));

        var ran = false;
        await SchemaMigrationLock.RunAsync(config, NullLogger.Instance, () => { ran = true; return Task.CompletedTask; },
            timeout: TimeSpan.FromSeconds(5));
        Assert.True(ran);
    }

    [PostgreSqlFact]
    public async Task A_replica_gives_up_with_a_clear_error_when_the_holder_never_finishes()
    {
        var config = Schema.BaseConfig();
        var holding = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = SchemaMigrationLock.RunAsync(config, NullLogger.Instance, async () =>
        {
            holding.SetResult();
            await release.Task;
        });
        await holding.Task;
        try
        {
            var waited = Stopwatch.StartNew();
            var error = await Assert.ThrowsAsync<TimeoutException>(() => SchemaMigrationLock.RunAsync(
                config, NullLogger.Instance, () => Task.CompletedTask, timeout: TimeSpan.FromSeconds(2)));
            Assert.Contains("held by another instance", error.Message);
            Assert.InRange(waited.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        }
        finally
        {
            release.SetResult();
            await holder;
        }
    }

    private static async Task StartTogetherAsync(Schema db, int replicas, bool chatEnabled = false)
    {
        // The backend builds a Kubernetes client from ~/.kube/config outside a cluster; /healthz
        // never needs to reach it.
        var home = Directory.CreateTempSubdirectory("agenthub-startup-");
        Directory.CreateDirectory(Path.Join(home.FullName, ".kube"));
        await File.WriteAllTextAsync(Path.Join(home.FullName, ".kube", "config"), """
            apiVersion: v1
            kind: Config
            clusters: [{name: none, cluster: {server: "http://127.0.0.1:1"}}]
            users: [{name: none, user: {token: unused}}]
            contexts: [{name: none, context: {cluster: none, user: none, namespace: agenthub-sessions}}]
            current-context: none
            """);
        var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var started = new List<Replica>();
        try
        {
            // Created first and started together: the point is that their migrations overlap.
            for (var i = 0; i < replicas; i++)
                started.Add(new Replica(db.ConnectionString, home.FullName, apiKey, chatEnabled));
            foreach (var replica in started) replica.Start();

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = Stopwatch.StartNew();
            var healthy = new bool[replicas];
            while (!healthy.All(h => h) && deadline.Elapsed < HealthyWithin)
            {
                for (var i = 0; i < replicas; i++)
                    if (!healthy[i] && !started[i].HasExited) healthy[i] = await started[i].IsHealthyAsync(http);
                if (started.Any(r => r.HasExited)) break;
                await Task.Delay(250);
            }

            for (var i = 0; i < replicas; i++)
                Assert.True(healthy[i],
                    $"replica {i + 1} of {replicas} not healthy after {deadline.Elapsed.TotalSeconds:0}s " +
                    $"(exited: {started[i].HasExited}):\n{started[i].Log}");
        }
        finally
        {
            foreach (var replica in started) await replica.DisposeAsync();
            home.Delete(recursive: true);
        }
    }

    /// <summary>One backend process with the quickstart's configuration: API key, no OIDC.</summary>
    private sealed class Replica : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly int _port = FreePort();
        private readonly System.Text.StringBuilder _log = new();

        public Replica(string connectionString, string home, string apiKey, bool chatEnabled)
        {
            var info = new ProcessStartInfo(Environment.ProcessPath is { } host && Path.GetFileNameWithoutExtension(host) == "dotnet" ? host : "dotnet")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            info.ArgumentList.Add(Path.Join(AppContext.BaseDirectory, "AgentHub.Api.dll"));
            info.Environment["HOME"] = home;
            info.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
            info.Environment["Kestrel__Endpoints__Http__Url"] = $"http://127.0.0.1:{_port}";
            info.Environment["ConnectionStrings__Postgres"] = connectionString;
            info.Environment["Auth__ApiKey"] = apiKey;
            info.Environment["AgentHub__Namespace"] = "agenthub-sessions";
            info.Environment["AgentHub__CallbackBaseUrl"] = $"http://127.0.0.1:{_port}";
            info.Environment["Browser__Enabled"] = "false";
            if (chatEnabled)
            {
                // With the relays on, every notifier and poll service is built and started, the
                // Telegram permission notifier of #371 included. Their calls go to a closed
                // local port: the proxy for the fixed Telegram host, ApiUrl for Signal.
                info.Environment["Chat__Telegram__Enabled"] = "true";
                info.Environment["Chat__Telegram__BotToken"] = "0:stand-in";
                info.Environment["Chat__Signal__Enabled"] = "true";
                info.Environment["Chat__Signal__ApiUrl"] = "http://127.0.0.1:1";
                info.Environment["Chat__Signal__Number"] = "+10000000000";
                info.Environment["HTTPS_PROXY"] = "http://127.0.0.1:1";
                info.Environment["NO_PROXY"] = "127.0.0.1,localhost";
            }
            _process = new Process { StartInfo = info };
            _process.OutputDataReceived += (_, e) => Append(e.Data);
            _process.ErrorDataReceived += (_, e) => Append(e.Data);
        }

        public bool HasExited => _process.HasExited;

        public string Log { get { lock (_log) return _log.ToString(); } }

        private void Append(string? line) { lock (_log) _log.AppendLine(line); }

        private bool _started;

        public void Start()
        {
            _process.Start();
            _started = true;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public async Task<bool> IsHealthyAsync(HttpClient http)
        {
            try { return (await http.GetAsync($"http://127.0.0.1:{_port}/healthz")).IsSuccessStatusCode; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet: the replica is still migrating or waiting for the lock.
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // A deadlocked host does not finish a graceful shutdown, so kill it outright.
            if (_started && !_process.HasExited) _process.Kill(entireProcessTree: true);
            if (_started) await _process.WaitForExitAsync();
            _process.Dispose();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class Schema : IAsyncDisposable
    {
        private readonly string _base;
        private readonly string _name;
        public string ConnectionString { get; }

        private Schema(string baseConnectionString, string name)
        {
            _base = baseConnectionString;
            _name = name;
            ConnectionString = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = name }.ConnectionString;
        }

        private static string Base() => Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)
            ?? throw new InvalidOperationException($"{PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} is required.");

        public static IConfiguration BaseConfig() => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = Base() })
            .Build();

        public static async Task<Schema> CreateAsync()
        {
            var schema = new Schema(Base(), $"startup_test_{Guid.NewGuid():N}");
            await schema.ExecAsync($"CREATE SCHEMA \"{schema._name}\"", onBase: true);
            return schema;
        }

        public async Task ExecAsync(string sql, bool onBase = false)
        {
            await using var connection = new NpgsqlConnection(onBase ? _base : ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> CountAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public ValueTask DisposeAsync() => new(ExecAsync($"DROP SCHEMA IF EXISTS \"{_name}\" CASCADE", onBase: true));
    }
}

public sealed class PostgreSqlTheoryAttribute : TheoryAttribute
{
    public PostgreSqlTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)))
            Skip = $"Set {PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable} to run PostgreSQL integration tests.";
    }
}
