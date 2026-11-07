using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using k8s;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class LegacyExecutionMigrationTests
{
    private const string Native = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"keep the native conversation\"}}\n";

    [PostgreSqlFact]
    public async Task NullLegacyNativeIdAdaptsAndStoredConversationRemainsReadable()
    {
        await using var database = await Scenario.CreateAsync();
        await database.AddAsync("s");
        var forbidden = await Assert.ThrowsAsync<PostgresException>(() => database.SqlAsync(
            "UPDATE sessions SET agent_session_id=NULL WHERE id='s'"));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, forbidden.SqlState);
        // A partial/older schema may lack the current constraint; exercise the actual NULL row.
        await database.SqlAsync("ALTER TABLE sessions ALTER COLUMN agent_session_id DROP NOT NULL; UPDATE sessions SET agent_session_id=NULL,claude_session_id=NULL WHERE id='s'");
        await database.Executions.InitializeAsync();
        var execution = await database.Executions.EnsureLegacyAsync("alice", "s");
        Assert.Equal("", execution!.NativeId);
        Assert.Equal(0, execution.Generation);
        var entries = await database.Service.GetConversationAsync("alice", "s");
        Assert.Equal("keep the native conversation", Assert.Single(entries!).Text);
        Assert.Equal("", (await database.Sessions.GetAsync("alice", "s"))!.AgentSessionId);
    }

    [PostgreSqlFact]
    public async Task RejectedLazyInsertReturnsNativeHistoryLogsAndCanRetry()
    {
        await using var database = await Scenario.CreateAsync();
        await database.AddAsync("s");
        await database.Executions.InitializeAsync();
        await database.SqlAsync("""
            CREATE FUNCTION reject_execution_insert() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'synthetic migration write unavailable' USING ERRCODE='42501'; END;
            $$;
            CREATE TRIGGER reject_execution_insert BEFORE INSERT ON session_executions
            FOR EACH ROW EXECUTE FUNCTION reject_execution_insert();
            """);
        var entries = await database.Service.GetConversationAsync("alice", "s");
        Assert.Equal("keep the native conversation", Assert.Single(entries!).Text);
        Assert.Contains(database.Logger.Messages, message => message.Contains("execution history") && message.Contains("s"));
        Assert.Null(await database.Executions.ReadAsync("alice", "s"));
        await database.SqlAsync("DROP TRIGGER reject_execution_insert ON session_executions");
        Assert.Single((await database.Service.GetConversationAsync("alice", "s"))!);
        Assert.NotNull(await database.Executions.ReadAsync("alice", "s"));
    }

    [PostgreSqlFact]
    public async Task MissingLedgerDoesNotBreakStoredOrLiveConversationReads()
    {
        await using var database = await Scenario.CreateAsync();
        await database.AddAsync("s");
        // Deliberately omit ledger initialization: schema rollout must not hide existing history.
        Assert.Single((await database.Service.GetConversationAsync("alice", "s"))!);
        IReadOnlyList<TranscriptEntry> current = [new("user", "live native conversation", null)];
        var page = await SessionTranscripts.PageAsync(database.Service, "alice", "s", "Running", 0, 40,
            default, _ => Task.FromResult<IReadOnlyList<TranscriptEntry>?>(current));
        Assert.Equal("live native conversation", Assert.Single(page.Entries).Text);
        Assert.NotEmpty(database.Logger.Messages);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => database.Service.ComposeExecutionHistoryAsync(
            "alice", "s", current, new CancellationToken(true)));
    }

    private sealed class HistoryLogger : ILogger<KubernetesSessionService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public required NpgsqlConnection Connection { get; init; }
        public required string Schema { get; init; }
        public required PostgresSessionStore Sessions { get; init; }
        public required PostgresSessionExecutionStore Executions { get; init; }
        public required KubernetesSessionService Service { get; init; }
        public required HistoryLogger Logger { get; init; }

        public static async Task<Scenario> CreateAsync()
        {
            var cs = Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING")!;
            var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();
            var schema = "legacy_execution_" + Guid.NewGuid().ToString("N");
            await using (var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection)) await command.ExecuteNonQueryAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["ConnectionStrings:Postgres"] = new NpgsqlConnectionStringBuilder(cs) { SearchPath = schema, Pooling = false }.ConnectionString
            }).Build();
            var sessions = new PostgresSessionStore(config);
            await sessions.InitializeAsync();
            var executions = new PostgresSessionExecutionStore(config);
            var logger = new HistoryLogger();
            var service = new KubernetesSessionService(config, sessions, null!, new NullArtifactStore(), null!, null!, null!,
                null!, null!, null!, null!, null!, logger,
                cluster: new Kubernetes(new KubernetesClientConfiguration { Host = "http://127.0.0.1:1" }), executions: executions);
            return new() { Connection = connection, Schema = schema, Sessions = sessions, Executions = executions, Service = service, Logger = logger };
        }
        public async Task AddAsync(string id)
        {
            await Sessions.UpsertAsync(new SessionRecord { Id = id, Owner = "alice", CallbackToken = "synthetic", AgentSessionId = "native" });
            await Sessions.SetTranscriptAsync(id, Native);
        }
        public async Task SqlAsync(string sql)
        {
            await using var command = new NpgsqlCommand($"SET search_path TO {Schema}; " + sql, Connection);
            await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        {
            await using var command = new NpgsqlCommand($"DROP SCHEMA {Schema} CASCADE", Connection);
            await command.ExecuteNonQueryAsync();
            await Connection.DisposeAsync();
        }
    }
}
