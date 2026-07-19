using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionDuplicationTests
{
    [Fact]
    public void DuplicateRequest_CopiesReusableFieldsAndExcludesState()
    {
        var source = SessionDuplication.CopyableRequest(new SessionRecord
        {
            Id = "old", Owner = "alice", Title = "Original", Mode = SessionMode.Autonomous,
            ClaudeSessionId = "old-claude", CallbackToken = "old-token", Status = "Succeeded",
            Prompt = "Run tests", AllowedToolsJson = "[\"Read\"]", ProjectId = "p1",
            McpConfigJson = "{\"mcpServers\":{}}"
        }, new DuplicateSessionRequest("Copy", "p2", false));

        Assert.Equal("Copy", source.Title);
        Assert.Equal("p2", source.ProjectId);
        Assert.Equal("Run tests", source.Prompt);
        Assert.Equal(["Read"], source.AllowedTools);
        Assert.Null(source.McpConfigJson);
    }

    [Fact]
    public void DuplicateRequest_CopiesAgentAuthAndPolicy()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = "alice", Title = "Codex", Mode = SessionMode.Autonomous,
            Agent = AgentKind.Codex, AuthMode = AgentAuthMode.ApiKey,
            AgentSessionId = "thread", CallbackToken = "token", Status = "Succeeded",
            AgentPolicyJson = "{\"allowedTools\":[\"Read\"],\"allowedMcpTools\":[],\"allowedCommands\":[\"git status\"]}"
        };

        var copy = SessionDuplication.CopyableRequest(source, new("Copy", null, false));

        Assert.Equal(AgentKind.Codex, copy.Agent);
        Assert.Equal(AgentAuthMode.ApiKey, copy.AuthMode);
        Assert.Equal(["git status"], copy.Policy.AllowedCommands);
    }

    [Fact]
    public void DuplicateRequest_AppliesExplicitAgentAuthAndPolicyOverrides()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = "alice", Title = "Claude", Mode = SessionMode.Autonomous,
            Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription,
            AgentSessionId = "thread", CallbackToken = "token"
        };
        var requestedPolicy = new AgentPolicy { AllowedCommands = ["npm test"] };

        var copy = SessionDuplication.CopyableRequest(source,
            new("Copy", null, false, AgentKind.Codex, AgentAuthMode.ApiKey, requestedPolicy));

        Assert.Equal(AgentKind.Codex, copy.Agent);
        Assert.Equal(AgentAuthMode.ApiKey, copy.AuthMode);
        Assert.Equal(["npm test"], copy.Policy.AllowedCommands);
    }

    [Fact]
    public void DuplicateRequest_ExplicitEmptyPolicyDoesNotRestoreLegacyAllowedTools()
    {
        var source = new SessionRecord
        {
            Id = "legacy", Owner = "alice", Title = "Legacy", Mode = SessionMode.Autonomous,
            Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Auto,
            AgentSessionId = "thread", CallbackToken = "token",
            AllowedToolsJson = "[\"Read\"]"
        };

        var copy = SessionDuplication.CopyableRequest(source,
            new("Copy", null, false, AgentKind.Claude, AgentAuthMode.Subscription, new AgentPolicy()));

        Assert.Empty(copy.Policy.AllowedTools);
        Assert.Empty(copy.AllowedTools);
    }

    [Fact]
    public void UpdateRequest_DistinguishesOmittedProjectFromExplicitRemoval()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var omitted = System.Text.Json.JsonSerializer.Deserialize<UpdateSessionRequest>("{}", options);
        var removed = System.Text.Json.JsonSerializer.Deserialize<UpdateSessionRequest>("{\"projectId\":null}", options);

        Assert.False(omitted!.ProjectIdSpecified);
        Assert.True(removed!.ProjectIdSpecified);
        Assert.Null(removed.ProjectId);
    }

    [Fact]
    public void DuplicateRequest_PreservesMigratedClaudeAutoAuthentication()
    {
        var source = new SessionRecord
        {
            Id = "legacy", Owner = "alice", Title = "Legacy", Mode = SessionMode.Interactive,
            Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Auto,
            AgentSessionId = "legacy-thread", CallbackToken = "token"
        };

        var copy = SessionDuplication.CopyableRequest(source, new("Copy", null, false));

        Assert.Equal(AgentKind.Claude, copy.Agent);
        Assert.Equal(AgentAuthMode.Auto, copy.AuthMode);
        AgentConfiguration.ValidateForDuplicatedSession(copy.Agent, copy.AuthMode);
    }}
