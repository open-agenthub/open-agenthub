using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using Xunit;

namespace AgentHub.Api.Tests;

public class AgentSessionResourceOrchestratorTests
{
    [Theory]
    [InlineData(AgentKind.Claude, "claude-state.tgz")]
    [InlineData(AgentKind.Codex, "codex-state.tgz")]
    public void PresignArtifactUrls_UsesSelectedProviderForPutAndResumeGet(
        AgentKind agent, string stateFile)
    {
        var artifacts = new RecordingArtifactStore();
        var record = Record(SessionMode.Interactive, agent);

        var urls = AgentSessionResourceOrchestrator.PresignArtifactUrls(
            artifacts, "alice", record, resume: true, TimeSpan.FromMinutes(5));

        var stateKey = $"sessions/alice/session-id/{stateFile}";
        Assert.Equal($"PUT:{stateKey}", urls.StatePutUrl);
        Assert.Equal($"GET:{stateKey}", urls.StateGetUrl);
        Assert.Equal("PUT:sessions/alice/session-id/scrollback.log", urls.ScrollbackPutUrl);
        Assert.Equal(new[]
        {
            $"PUT:{stateKey}",
            $"GET:{stateKey}",
            "PUT:sessions/alice/session-id/scrollback.log"
        }, artifacts.Presigned);
    }

    [Fact]
    public void PresignArtifactUrls_SwitchingAgentCannotLoadOrOverwriteOtherProviderState()
    {
        var artifacts = new RecordingArtifactStore();

        var claude = AgentSessionResourceOrchestrator.PresignArtifactUrls(
            artifacts, "alice", Record(SessionMode.Interactive, AgentKind.Claude),
            resume: true, TimeSpan.FromMinutes(5));
        var codex = AgentSessionResourceOrchestrator.PresignArtifactUrls(
            artifacts, "alice", Record(SessionMode.Interactive, AgentKind.Codex),
            resume: true, TimeSpan.FromMinutes(5));

        Assert.NotEqual(claude.StatePutUrl, codex.StatePutUrl);
        Assert.NotEqual(claude.StateGetUrl, codex.StateGetUrl);
        Assert.Contains("claude-state.tgz", claude.StatePutUrl);
        Assert.Contains("claude-state.tgz", claude.StateGetUrl);
        Assert.Contains("codex-state.tgz", codex.StatePutUrl);
        Assert.Contains("codex-state.tgz", codex.StateGetUrl);
    }

    [Theory]
    [InlineData(SessionMode.Autonomous)]
    [InlineData(SessionMode.Scheduled)]
    public async Task PrepareAsync_MissingCredentialFailsBeforeCreatingAnySessionResource(SessionMode mode)
    {
        var events = new List<string>();

        var result = await AgentSessionResourceOrchestrator.PrepareAsync(
            Record(mode), Context(),
            (diagnostic, _) =>
            {
                events.Add($"failed:{diagnostic}");
                return Task.CompletedTask;
            },
            _ =>
            {
                events.Add("resource-created");
                return Task.FromResult(true);
            },
            CancellationToken.None);

        Assert.False(result.ShouldSpawn);
        Assert.False(result.HasGitCredentials);
        Assert.Equal(new[]
        {
            $"failed:[agent] Cannot start Codex {mode} session: Subscription credential is not stored."
        }, events);
    }

    [Fact]
    public async Task PrepareAsync_InteractiveBypassesPreflightAndCreatesSessionResources()
    {
        var failureCalls = 0;
        var resourceCalls = 0;

        var result = await AgentSessionResourceOrchestrator.PrepareAsync(
            Record(SessionMode.Interactive), Context(),
            (_, _) => { failureCalls++; return Task.CompletedTask; },
            _ => { resourceCalls++; return Task.FromResult(true); },
            CancellationToken.None);

        Assert.True(result.ShouldSpawn);
        Assert.True(result.HasGitCredentials);
        Assert.Equal(0, failureCalls);
        Assert.Equal(1, resourceCalls);
    }

    private static SessionRecord Record(SessionMode mode, AgentKind agent = AgentKind.Codex) => new()
    {
        Id = "session-id",
        Owner = "owner",
        Title = "Session",
        Mode = mode,
        Agent = agent,
        AuthMode = AgentAuthMode.Subscription,
        AgentSessionId = "agent-session-id",
        CallbackToken = "callback-token"
    };

    private static PodBuildContext Context() => new()
    {
        Owner = "owner",
        CredentialsSecretName = "creds-owner",
        ClaudeCredentialSecretName = "claude-owner",
        CodexCredentialSecretName = "codex-owner",
        CallbackUrl = "http://callback/internal/sessions/session-id",
        StatePutUrl = "http://s3/state-put",
        StateGetUrl = "",
        ScrollbackPutUrl = "http://s3/scroll-put",
        RuntimeImages = new AgentRuntimeImages("runtime-claude", "runtime-codex", "Always")
    };

    private sealed class RecordingArtifactStore : IArtifactStore
    {
        public List<string> Presigned { get; } = new();

        public string PresignPut(string key, TimeSpan ttl)
        {
            var value = $"PUT:{key}";
            Presigned.Add(value);
            return value;
        }

        public string PresignGet(string key, TimeSpan ttl)
        {
            var value = $"GET:{key}";
            Presigned.Add(value);
            return value;
        }

        public Task<string?> GetTextAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
    }
}
