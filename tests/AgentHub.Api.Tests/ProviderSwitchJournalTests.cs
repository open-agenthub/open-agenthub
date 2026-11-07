using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class ProviderSwitchJournalTests
{
    private static ProviderSwitchRequest Request(Guid? id = null) => new(id ?? Guid.NewGuid(), "s", 0,
        AgentKind.Claude, "source-pod", "workspace-uid", new(AgentKind.Codex, AgentAuthMode.Subscription,
            "target-account", null, null), "Reviewed handover. Omitted: native resume state.");

    [PostgreSqlFact]
    public async Task AdmissionIsOwnedExclusiveDurableAndRotatesAuthorityExactlyOnce()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await database.Sessions.UpsertAsync(new SessionRecord { Id = "s", Owner = "alice", CallbackToken = "source-authority",
            AgentSessionId = "source-native", Agent = AgentKind.Claude });
        var workspace = new PostgresSessionWorkspaceStore(database.Configuration);
        await workspace.InitializeAsync();
        await workspace.AddAsync("alice", new("s", "workspace-s", null, "10Gi", null, "nonce", "[]"));
        var fence = new PostgresExecutionFence(database.Configuration);
        var journal = new PostgresProviderSwitchStore(database.Configuration, fence);
        await journal.InitializeAsync();
        var request = Request();
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.CreateAsync("bob", request));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { await journal.CreateAsync("alice", request); return true; }
            catch (InvalidOperationException) { return false; }
        }));
        Assert.Single(attempts, success => success);
        Assert.Null(await journal.GetAsync("bob", "s"));
        var reopened = new PostgresProviderSwitchStore(database.Configuration, fence);
        Assert.Equal(request, (await reopened.GetAsync("alice", "s"))!.Request);
        Assert.True((await database.Sessions.GetAsync("alice", "s"))!.ProviderSwitchPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AdmitTargetAsync("alice", "s", request.RequestId));
        await reopened.MoveAsync("alice", "s", request.RequestId, "requested", new("prepared", SourceNativeId: "source-native"));
        await reopened.MoveAsync("alice", "s", request.RequestId, "prepared", new("source-stopped", SourceNativeId: "source-native"));
        await using (var callback = await fence.ReadAsync("s", default))
        {
            var admission = reopened.AdmitTargetAsync("alice", "s", request.RequestId);
            await Task.Delay(100);
            Assert.False(admission.IsCompleted);
            await callback.DisposeAsync();
            await admission.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var target = (await database.Sessions.GetAsync("alice", "s"))!;
        Assert.Equal(1, target.ExecutionGeneration);
        Assert.Equal(AgentKind.Codex, target.Agent);
        Assert.Equal(AgentAuthMode.Subscription, target.AuthMode);
        Assert.Equal("target-account", target.CredentialId);
        Assert.Equal("target-account", target.ResolvedCredentialId);
        Assert.NotEqual("source-native", target.AgentSessionId);
        Assert.NotEqual("source-authority", target.CallbackToken);
        Assert.True(target.ProviderSwitchPending);
        Assert.Equal("target-admitted", (await reopened.GetAsync("alice", "s"))!.Progress.Phase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AdmitTargetAsync("alice", "s", request.RequestId));
        Assert.Equal(target.CallbackToken, (await database.Sessions.GetAsync("alice", "s"))!.CallbackToken);
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.MoveAsync("alice", "s", request.RequestId, "target-admitted", new("cancelled")));
        await reopened.MoveAsync("alice", "s", request.RequestId, "target-admitted", new("target-starting"));
        await reopened.MoveAsync("alice", "s", request.RequestId, "target-starting", new("target-started", TargetPodUid: "target-pod"));
        var confirmedNative = Guid.NewGuid().ToString();
        await reopened.MoveAsync("alice", "s", request.RequestId, "target-started", new("committing", TargetPodUid: "target-pod", TargetNativeId: confirmedNative));
        Assert.Equal(confirmedNative, (await database.Sessions.GetAsync("alice", "s"))!.AgentSessionId);
        await reopened.MoveAsync("alice", "s", request.RequestId, "committing", new("completed", TargetNativeId: confirmedNative));
        Assert.False((await database.Sessions.GetAsync("alice", "s"))!.ProviderSwitchPending);
        await database.Sessions.DeleteAsync("s");
        Assert.Null(await reopened.GetAsync("alice", "s"));
        Assert.Empty(await reopened.PendingAsync());
    }

    [PostgreSqlFact]
    public async Task CancellationRetainsSourceAuthorityAndFailedWorkRequiresExplicitRetry()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await database.Sessions.UpsertAsync(new SessionRecord { Id = "s", Owner = "alice", CallbackToken = "source-authority", AgentSessionId = "native" });
        var workspace = new PostgresSessionWorkspaceStore(database.Configuration);
        await workspace.InitializeAsync();
        await workspace.AddAsync("alice", new("s", "workspace-s", null, "10Gi", null, "nonce", "[]"));
        var journal = new PostgresProviderSwitchStore(database.Configuration, new PostgresExecutionFence(database.Configuration));
        await journal.InitializeAsync();
        var request = Request();
        await journal.CreateAsync("alice", request);
        await journal.MoveAsync("alice", "s", request.RequestId, "requested", new("requested", Error: "source busy"));
        Assert.Empty(await journal.PendingAsync());
        Assert.True((await database.Sessions.GetAsync("alice", "s"))!.ProviderSwitchPending);
        await journal.MoveAsync("alice", "s", request.RequestId, "requested", new("requested"));
        Assert.Single(await journal.PendingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.MoveAsync("bob", "s", request.RequestId, "requested", new("cancelled")));
        await journal.MoveAsync("alice", "s", request.RequestId, "requested", new("cancelled"));
        var source = (await database.Sessions.GetAsync("alice", "s"))!;
        Assert.False(source.ProviderSwitchPending);
        Assert.Equal(0, source.ExecutionGeneration);
        Assert.Equal("source-authority", source.CallbackToken);
        Assert.Equal(AgentKind.Claude, source.Agent);
        await journal.CreateAsync("alice", Request());
        Assert.Single(await journal.PendingAsync());
    }
}
