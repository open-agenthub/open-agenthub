using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>The agent pod learns via env whether the network MCP server is available.</summary>
public class NetworkPodEnvTests
{
    [Theory]
    [InlineData(true, SessionMode.Interactive, "1")]
    [InlineData(true, SessionMode.Autonomous, "1")]
    [InlineData(true, SessionMode.Scheduled, "0")] // like browser/spawn: nobody could approve
    [InlineData(false, SessionMode.Interactive, "0")]
    public void Build_SetsNetworkMcpEnabledEnv(bool enabled, SessionMode mode, string expected)
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
            HasSelectedApiKey = true,
            CallbackUrl = "http://callback/internal/sessions/session-id",
            StatePutUrl = "", StateGetUrl = "", ScrollbackPutUrl = "", TranscriptPutUrl = "",
            RuntimeImages = new AgentRuntimeImages("claude", "codex", "cursor", "openclaw", "Always"),
            Runtime = new AgentPodRuntimeSettings { NetworkMcpEnabled = enabled }
        };

        var pod = AgentPodSpecFactory.Build(record, request, context);
        var container = Assert.Single(pod.Containers);
        var env = Assert.Single(container.Env, e => e.Name == "AGENTHUB_NETWORK_MCP_ENABLED");
        Assert.Equal(expected, env.Value);
    }
}
