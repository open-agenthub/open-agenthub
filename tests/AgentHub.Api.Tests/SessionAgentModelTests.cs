using AgentHub.Api.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionAgentModelTests
{
    [Fact]
    public void NewRequest_DefaultsToClaudeSubscription()
    {
        var request = new CreateSessionRequest();

        Assert.Equal(AgentKind.Claude, request.Agent);
        Assert.Equal(AgentAuthMode.Subscription, request.AuthMode);
    }

    [Theory]
    [InlineData(AgentAuthMode.Auto)]
    [InlineData((AgentAuthMode)99)]
    public void CreateConfiguration_RejectsNonExplicitAuthenticationModes(AgentAuthMode authMode)
    {
        Assert.Throws<ArgumentException>(() => AgentConfiguration.ValidateForCreate(AgentKind.Claude, authMode));
    }

    [Fact]
    public void NumericAuthenticationModeFromHttpPayload_IsRejectedBeforeSessionCreation()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var request = System.Text.Json.JsonSerializer.Deserialize<CreateSessionRequest>("{\"authMode\":99}", options)!;

        Assert.Equal((AgentAuthMode)99, request.AuthMode);
        Assert.Throws<ArgumentException>(() => AgentConfiguration.ValidateForCreate(request.Agent, request.AuthMode));
    }

    [Theory]
    [InlineData((AgentKind)99)]
    [InlineData((AgentKind)(-1))]
    public void CreateConfiguration_RejectsUnknownAgentKinds(AgentKind agent)
    {
        Assert.Throws<ArgumentException>(() => AgentConfiguration.ValidateForCreate(agent, AgentAuthMode.Subscription));
    }

    [Theory]
    [InlineData(AgentAuthMode.Auto)]
    [InlineData((AgentAuthMode)99)]
    public void UpdateConfiguration_RejectsNonExplicitAuthenticationModes(AgentAuthMode authMode)
    {
        Assert.Throws<ArgumentException>(() => AgentConfiguration.ValidateForUpdate(AgentKind.Codex, authMode));
    }

    [Fact]
    public void DuplicatedCodexSession_CannotUseAutoAuthentication()
    {
        Assert.Throws<ArgumentException>(() => AgentConfiguration.ValidateForDuplicatedSession(AgentKind.Codex, AgentAuthMode.Auto));
    }

    [Fact]
    public void EmptyStructuredPolicy_UsesLegacyAllowedTools()
    {
        var policy = AgentConfiguration.ResolvePolicy(new AgentPolicy(), ["Read", "Write"]);

        Assert.Equal(["Read", "Write"], policy.AllowedTools);
        Assert.Empty(policy.AllowedMcpTools);
        Assert.Empty(policy.AllowedCommands);
    }

    [Fact]
    public void StructuredPolicy_TakesPrecedenceOverLegacyAllowedTools()
    {
        var policy = AgentConfiguration.ResolvePolicy(
            new AgentPolicy { AllowedCommands = ["git status"] },
            ["Read"]);

        Assert.Empty(policy.AllowedTools);
        Assert.Equal(["git status"], policy.AllowedCommands);
    }
}
