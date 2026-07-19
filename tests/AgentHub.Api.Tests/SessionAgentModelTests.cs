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
    public void PartialUpdate_AllowsMigratedClaudeAutoOnlyWhenAgentAndAuthAreOmitted()
    {
        AgentConfiguration.ValidateForUpdate(AgentKind.Claude, AgentAuthMode.Auto, null, null);
    }

    [Theory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    public void PartialUpdate_RejectsAnyAgentFieldThatLeavesMigratedAutoEffective(AgentKind requestedAgent)
    {
        Assert.Throws<ArgumentException>(() => AgentConfiguration.ValidateForUpdate(
            AgentKind.Claude, AgentAuthMode.Auto, requestedAgent, null));
    }

    [Fact]
    public void PartialUpdate_AcceptsAnExplicitAuthThatMakesTheEffectivePairPublic()
    {
        AgentConfiguration.ValidateForUpdate(
            AgentKind.Claude, AgentAuthMode.Auto, AgentKind.Codex, AgentAuthMode.ApiKey);
    }

    [Fact]
    public void DuplicatedCodexSession_CannotUseAutoAuthentication()
    {
        Assert.Throws<ArgumentException>(() => AgentConfiguration.ValidateForDuplicatedSession(AgentKind.Codex, AgentAuthMode.Auto));
    }

    [Fact]
    public void OmittedStructuredPolicy_UsesLegacyAllowedTools()
    {
        var policy = AgentConfiguration.ResolvePolicy(null, ["Read", "Write"]);

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

    [Fact]
    public void CreateRequest_OmittedPolicyFallsBackToLegacyAllowedTools()
    {
        var request = DeserializeCreateRequest("{\"allowedTools\":[\"Read\"]}");

        Assert.Null(request.Policy);
        var policy = AgentConfiguration.ResolvePolicy(request.Policy, request.AllowedTools);
        Assert.Equal(["Read"], policy.AllowedTools);
    }

    [Theory]
    [InlineData("{\"policy\":{},\"allowedTools\":[\"Read\"]}")]
    [InlineData("{\"policy\":{\"allowedTools\":[],\"allowedMcpTools\":[],\"allowedCommands\":[]},\"allowedTools\":[\"Read\"]}")]
    public void CreateRequest_ExplicitEmptyPolicyOverridesLegacyAllowedTools(string json)
    {
        var request = DeserializeCreateRequest(json);

        Assert.NotNull(request.Policy);
        var policy = AgentConfiguration.ResolvePolicy(request.Policy, request.AllowedTools);
        Assert.Empty(policy.AllowedTools);
        Assert.Empty(policy.AllowedMcpTools);
        Assert.Empty(policy.AllowedCommands);
    }

    [Fact]
    public void CreateRequest_OmittedPolicyAndLegacyToolsResolveToEmptyPolicy()
    {
        var request = DeserializeCreateRequest("{}");

        Assert.Null(request.Policy);
        var policy = AgentConfiguration.ResolvePolicy(request.Policy, request.AllowedTools);
        Assert.Empty(policy.AllowedTools);
        Assert.Empty(policy.AllowedMcpTools);
        Assert.Empty(policy.AllowedCommands);
    }

    [Fact]
    public void UpdateRequest_DistinguishesOmittedFromExplicitEmptyPolicy()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var omitted = System.Text.Json.JsonSerializer.Deserialize<UpdateSessionRequest>("{}", options)!;
        var explicitEmpty = System.Text.Json.JsonSerializer.Deserialize<UpdateSessionRequest>(
            "{\"policy\":{\"allowedTools\":[],\"allowedMcpTools\":[],\"allowedCommands\":[]}}", options)!;

        Assert.Null(omitted.Policy);
        Assert.NotNull(explicitEmpty.Policy);
        Assert.Empty(explicitEmpty.Policy.AllowedTools);
    }

    private static CreateSessionRequest DeserializeCreateRequest(string json)
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        return System.Text.Json.JsonSerializer.Deserialize<CreateSessionRequest>(json, options)!;
    }
}
