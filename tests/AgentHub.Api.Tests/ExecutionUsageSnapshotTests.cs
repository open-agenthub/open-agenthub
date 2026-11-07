using AgentHub.Api.Models;
using AgentHub.Api.Otel;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Usage;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHub.Api.Tests;

public class ExecutionUsageSnapshotTests
{
    [PostgreSqlFact]
    public async Task SealedUsageKeepsProviderAccountAndDeltasAcrossReturnExecutions()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await database.Sessions.UpsertAsync(new() { Id = "s", Owner = "alice", CallbackToken = "source", AgentSessionId = "native",
            Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription, ResolvedCredentialId = "first-account" });
        var usage = new PostgresUsageStore(database.Configuration);
        await usage.InitializeAsync();
        var snapshots = new PostgresExecutionUsageSnapshots(database.Configuration);
        await snapshots.InitializeAsync();
        await usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "s", InputTokens = 100, OutputTokens = 10 });
        await snapshots.CaptureAsync("alice", "s", 0);
        var first = Assert.Single(await snapshots.ReadAsync("alice", "s"));
        Assert.Equal("Claude", first.Agent); Assert.Equal("first-account", first.CredentialId);
        Assert.Equal(100, first.InputTokens);
        await using var connection = new NpgsqlConnection(database.Configuration.GetConnectionString("Postgres"));
        await connection.OpenAsync();
        await using (var update = new NpgsqlCommand("UPDATE sessions SET execution_generation=1,agent='Codex',resolved_credential_id='second-account' WHERE id='s'", connection)) await update.ExecuteNonQueryAsync();
        await usage.AddReportedExecutionUsageAsync("s", "alice", 1, "codex", UsageBilling.Subscription, new(150, 20, 0, 0, 0, 1, null));
        await snapshots.CaptureAsync("alice", "s", 1);
        var completed = await snapshots.ReadAsync("alice", "s");
        Assert.Equal(2, completed.Count); Assert.Equal("Codex", completed[1].Agent);
        Assert.Equal("second-account", completed[1].CredentialId); Assert.Equal(150, completed[1].InputTokens);
        Assert.Equal(20, completed[1].OutputTokens);
        await using (var update = new NpgsqlCommand("UPDATE sessions SET execution_generation=2,agent='Claude',resolved_credential_id='third-account' WHERE id='s'", connection)) await update.ExecuteNonQueryAsync();
        await usage.AddDeltaAsync(new SessionUsageDelta { SessionId = "s", InputTokens = 70, OutputTokens = 8 });
        await snapshots.CaptureAsync("alice", "s", 2);
        Assert.Equal(70, (await snapshots.ReadAsync("alice", "s"))[2].InputTokens);
        Assert.Empty(await snapshots.ReadAsync("bob", "s"));
        Assert.Equal(320, (await usage.GetAsync("alice", "s"))!.InputTokens);
        await database.Sessions.DeleteAsync("s");
        Assert.Empty(await snapshots.ReadAsync("alice", "s"));
    }
}
