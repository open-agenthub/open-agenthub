using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionExecutionTests
{
    [Fact]
    public void PreviewRetainsInstructionsReferencesAndReportsUnknownCapacity()
    {
        var result = HandoverPreviewBuilder.Build(new(AgentKind.Codex, "/keep instructions verbatim", "finish work",
            References: ["src/file.cs:12", "https://example.com/spec"]));
        Assert.Contains("/keep instructions verbatim", result.Text);
        Assert.Contains("src/file.cs:12", result.Text);
        Assert.Contains("Omitted:", result.Text);
        Assert.Contains("coverage is unknown", result.Text);
        Assert.Equal("unknown", result.Capacity);
        Assert.False(result.SwitchingAvailable);
        Assert.False(result.Text.StartsWith('/'));
        Assert.Equal(result.Text.Length, result.Characters);
        Assert.Throws<ArgumentException>(() => HandoverPreviewBuilder.Build(new(AgentKind.Codex, new string('a', 16000), "work")));
        Assert.Throws<ArgumentException>(() => HandoverPreviewBuilder.Build(new(AgentKind.Claude, "\u001b[2J", "work")));
        Assert.Throws<ArgumentException>(() => HandoverPreviewBuilder.Build(new(AgentKind.OpenCode, "instructions", "work")));
    }

    [Fact]
    public void CompositeIdentityTracksActiveProjectionAndHistoryRemainsBounded()
    {
        var execution = new SessionExecution(1, AgentKind.Codex, "native", null, "transcript", "state", false);
        var history = new ExecutionHistory(execution, [new("user", "old", null)], false, "");
        var first = ExecutionConversation.Compose(history, [new("assistant", "new", null) { SourceId = "projection-a" }]);
        var next = ExecutionConversation.Compose(history, [new("assistant", "new", null) { SourceId = "projection-b" }]);
        Assert.Equal("old", first[0].Text);
        Assert.Contains(first, e => e.Role == "boundary" && e.Generation == 1);
        Assert.NotEqual(ConversationPage.FromEntries("s", "Running", first, 0, 10).ConversationId,
            ConversationPage.FromEntries("s", "Running", next, 0, 10).ConversationId);
        var bounded = ExecutionConversation.Compose(history,
            Enumerable.Range(0, 6000).Select(i => new TranscriptEntry("assistant", "entry " + i, null)).ToArray());
        Assert.Contains("omitted", bounded[0].Text);
        Assert.Equal("entry 5999", bounded[^1].Text);
        Assert.True(bounded.Count <= 5001);
    }

    [PostgreSqlFact]
    public async Task DurableExecutionAdvanceIsAtomicScopedAndRejectsLateWrites()
    {
        var connectionString = Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING")!;
        var schema = "execution_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", connection)) await create.ExecuteNonQueryAsync();
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["ConnectionStrings:Postgres"] = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema, Pooling = false }.ConnectionString
            }).Build();
            var sessions = new PostgresSessionStore(config);
            var store = new PostgresSessionExecutionStore(config);
            await sessions.InitializeAsync(); await store.InitializeAsync();
            await sessions.UpsertAsync(new SessionRecord { Id = "s", Owner = "alice", CallbackToken = "synthetic", AgentSessionId = "same-id" });
            var initial = await store.EnsureLegacyAsync("alice", "s");
            Assert.Equal(0, initial!.Generation);
            Assert.EndsWith("/claude-state.tgz", initial.StateKey);
            Assert.StartsWith("sessions/u-", initial.StateKey);
            Assert.Null(await store.ReadAsync("bob", "s"));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => store.AdvanceAsync("bob", "s", 0, AgentKind.Codex, "same-id", null, []));
            var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
            {
                try { await store.AdvanceAsync("alice", "s", 0, AgentKind.Codex, "same-id", "account-ref", [new("user", "old", null)]); return true; }
                catch (InvalidOperationException) { return false; }
            }));
            Assert.Single(attempts, x => x);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveTranscriptAsync("alice", "s", 0, "late"));
            await store.SaveTranscriptAsync("alice", "s", 1, "native-current");
            var restored = await new PostgresSessionExecutionStore(config).ReadAsync("alice", "s");
            Assert.Equal(1, restored!.Current.Generation);
            Assert.Equal("old", restored.Previous.Single().Text);
            Assert.Equal(0, restored.Previous.Single().Generation);
            Assert.Equal("native-current", restored.CurrentTranscript);
            Assert.Contains("/executions/1/", restored.Current.TranscriptKey);
            await store.AdvanceAsync("alice", "s", 1, AgentKind.Claude, "same-id", null, [new("assistant", "second", null)]);
            var returned = await store.ReadAsync("alice", "s");
            Assert.Equal(2, returned!.Current.Generation);
            Assert.Equal("Provider execution: Codex", returned.Previous[1].Text);
            Assert.NotEqual(initial.StateKey, returned.Current.StateKey);
            await sessions.DeleteAsync("s");
            Assert.Null(await store.ReadAsync("alice", "s"));
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {schema}.session_execution_entries", connection);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally { await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection); await drop.ExecuteNonQueryAsync(); }
    }
}
