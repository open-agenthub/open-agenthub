using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class AgentSessionResourceOrchestratorTests
{
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

    private static SessionRecord Record(SessionMode mode) => new()
    {
        Id = "session-id",
        Owner = "owner",
        Title = "Session",
        Mode = mode,
        Agent = AgentKind.Codex,
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
}
