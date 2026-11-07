using System.Diagnostics;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using k8s.Models;
using Xunit;
using Npgsql;
using Microsoft.Extensions.Configuration;

namespace AgentHub.Api.Tests;

public class PersistentWorkspaceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BootstrapPreservesDirtyCheckoutAndNeverReentersClone(bool complete)
    {
        var root = Path.Combine(Path.GetTempPath(), "persistent-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "repo"));
        try
        {
            var git = new ProcessStartInfo("git") { WorkingDirectory = Path.Combine(root, "repo"), RedirectStandardOutput = true, RedirectStandardError = true };
            git.ArgumentList.Add("init");
            using (var process = Process.Start(git)!) { await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); }
            await File.WriteAllTextAsync(Path.Combine(root, "repo", "dirty.txt"), "preserve dirty and untracked contents\n");
            if (complete) await File.WriteAllTextAsync(Path.Combine(root, ".agenthub-workspace"), "fingerprint\n");
            var script = SessionWorkspaceVolumes.BootstrapGuard.Replace("/workspace", root)
                + "\nprintf 'clone reentered' > '" + root + "/clone-entered'\nexit 99\n";
            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(script);
            start.Environment["WORKSPACE_FINGERPRINT"] = "fingerprint";
            using var shell = Process.Start(start)!;
            await shell.WaitForExitAsync();
            Assert.Equal(complete ? 0 : 1, shell.ExitCode);
            Assert.False(File.Exists(Path.Combine(root, "clone-entered")));
            Assert.Equal("preserve dirty and untracked contents\n", await File.ReadAllTextAsync(Path.Combine(root, "repo", "dirty.txt")));
            Assert.True(Directory.Exists(Path.Combine(root, "repo", ".git")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ClaimUidAndAdmissionNoncePreventAdoptingAReplacement()
    {
        var workspace = new SessionWorkspace("s", "workspace-s", "uid-one", "10Gi", null, "nonce", "[]");
        var claim = new V1PersistentVolumeClaim { Metadata = new V1ObjectMeta { Name = workspace.ClaimName, Uid = "uid-one",
            Labels = new Dictionary<string,string> { [SessionWorkspaceVolumes.NonceLabel] = "nonce", ["agenthub.dev/session"] = "s", ["agenthub.dev/component"] = "workspace" } } };
        SessionWorkspaceVolumes.Verify(workspace, claim);
        claim.Metadata.Uid = "uid-two";
        Assert.Throws<InvalidOperationException>(() => SessionWorkspaceVolumes.Verify(workspace, claim));
        claim.Metadata.Uid = "uid-one"; claim.Metadata.Labels[SessionWorkspaceVolumes.NonceLabel] = "other";
        Assert.Throws<InvalidOperationException>(() => SessionWorkspaceVolumes.Verify(workspace, claim));
    }

    [PostgreSqlFact]
    public async Task TargetSnapshotsRemainSeparateUntilTheSourceHistoryIsSealed()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await database.Sessions.UpsertAsync(new SessionRecord { Id = "s", Owner = "alice", CallbackToken = "source", AgentSessionId = "native" });
        await database.Sessions.SetTranscriptAsync("s", "source-transcript");
        var executions = new PostgresSessionExecutionStore(database.Configuration);
        await executions.InitializeAsync(); await executions.EnsureLegacyAsync("alice", "s");
        await using var connection = new NpgsqlConnection(database.Configuration.GetConnectionString("Postgres"));
        await connection.OpenAsync();
        await using (var stage = new NpgsqlCommand("UPDATE sessions SET execution_generation=1,provider_switch_pending=TRUE WHERE id='s'", connection)) await stage.ExecuteNonQueryAsync();
        await executions.SaveTranscriptAsync("alice", "s", 1, "target-transcript");
        Assert.Equal("source-transcript", await database.Sessions.GetTranscriptAsync("s"));
        Assert.Equal(0, (await executions.ReadAsync("alice", "s"))!.Current.Generation);
        Assert.Equal("target-transcript", (await executions.SnapshotAsync("alice", "s", 1))!.Transcript);
        await executions.AdvanceAsync("alice", "s", 0, AgentKind.Codex, "target-native", null, [new("user", "source", null)]);
        Assert.Equal("target-transcript", (await executions.ReadAsync("alice", "s"))!.CurrentTranscript);
    }

    [PostgreSqlFact]
    public async Task WorkspaceOwnershipAndBoundClaimIdentitySurviveStoreRestartAndDelete()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await database.Sessions.UpsertAsync(new SessionRecord { Id = "s", Owner = "alice", CallbackToken = "synthetic", AgentSessionId = "native" });
        var store = new PostgresSessionWorkspaceStore(database.Configuration);
        await store.InitializeAsync();
        await store.AddAsync("alice", new("s", "workspace-s", null, "10Gi", "class-one", "nonce", "[]"));
        Assert.Null(await store.GetAsync("bob", "s"));
        await store.BindAsync("alice", "s", "uid-one");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.BindAsync("alice", "s", "uid-two"));
        var reopened = await new PostgresSessionWorkspaceStore(database.Configuration).GetAsync("alice", "s");
        Assert.Equal("uid-one", reopened!.ClaimUid);
        Assert.Equal("class-one", reopened.StorageClass);
        await database.Sessions.DeleteAsync("s");
        Assert.Null(await store.GetAsync("alice", "s"));
    }
}
