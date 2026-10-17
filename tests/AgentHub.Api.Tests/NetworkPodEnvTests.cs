using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>The agent pod learns via env whether the network MCP server and the container tools are available.</summary>
public class NetworkPodEnvTests
{
    [Theory]
    [InlineData(true, SessionMode.Interactive, "1")]
    [InlineData(true, SessionMode.Autonomous, "1")]
    [InlineData(true, SessionMode.Scheduled, "0")] // like browser/spawn: nobody could approve
    [InlineData(false, SessionMode.Interactive, "0")]
    public void Build_SetsNetworkMcpEnabledEnv(bool enabled, SessionMode mode, string expected)
    {
        var env = Env(mode, new AgentPodRuntimeSettings { NetworkMcpEnabled = enabled }, "AGENTHUB_NETWORK_MCP_ENABLED");
        Assert.Equal(expected, env);
    }

    // The container tools live on agenthub_sessions (docs/session-containers.md), so they need
    // that server as well; a scheduled run is a job that ends and must not leave helpers behind.
    [Theory]
    [InlineData(true, true, SessionMode.Interactive, "1")]
    [InlineData(true, true, SessionMode.Autonomous, "1")]
    [InlineData(true, true, SessionMode.Scheduled, "0")]
    [InlineData(true, false, SessionMode.Interactive, "0")]
    [InlineData(false, true, SessionMode.Interactive, "0")]
    public void Build_SetsContainersEnabledEnv(bool containers, bool spawnMcp, SessionMode mode, string expected) =>
        Assert.Equal(expected, Env(mode,
            new AgentPodRuntimeSettings { ContainersEnabled = containers, SpawnMcpEnabled = spawnMcp }, "AGENTHUB_CONTAINERS_ENABLED"));

    private static string Env(SessionMode mode, AgentPodRuntimeSettings runtime, string name)
    {
        var request = new CreateSessionRequest
        {
            Agent = AgentKind.Claude,
            AuthMode = AgentAuthMode.ApiKey,
            Mode = mode,
            Prompt = "p",
            Schedule = mode == SessionMode.Scheduled ? "0 * * * *" : null
        };
        var record = new SessionRecord
        {
            Id = "session-id", Owner = "owner", Title = "Session", Mode = mode,
            Agent = AgentKind.Claude, AuthMode = AgentAuthMode.ApiKey,
            AgentSessionId = "agent-session-id", CallbackToken = "callback-token"
        };
        var context = new PodBuildContext
        {
            Owner = "owner",
            CredentialsSecretName = "creds-owner",
            ClaudeCredentialSecretName = "claude-owner",
            CodexCredentialSecretName = "codex-owner",
            CursorCredentialSecretName = "cursor-owner",
            OpenClawCredentialSecretName = "openclaw-owner",
            OpenCodeCredentialSecretName = "opencode-owner",
            HasSelectedApiKey = true,
            CallbackUrl = "http://callback/internal/sessions/session-id",
            StatePutUrl = "", StateGetUrl = "", ScrollbackPutUrl = "", TranscriptPutUrl = "",
            RuntimeImages = new AgentRuntimeImages("claude", "codex", "cursor", "openclaw", "opencode", "Always"),
            Runtime = runtime
        };

        var pod = AgentPodSpecFactory.Build(record, request, context);
        var container = Assert.Single(pod.Containers);
        return Assert.Single(container.Env, e => e.Name == name).Value;
    }
}
