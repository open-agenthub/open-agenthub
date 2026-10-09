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
        Assert.Null(source.Policy);
        Assert.Equal(["Read"],
            AgentConfiguration.ResolvePolicy(source.Policy, source.AllowedTools).AllowedTools);
        Assert.Null(source.McpConfigJson);
    }

    /// <summary>An account belongs to one provider: it follows the copy only while the agent does.</summary>
    [Fact]
    public void DuplicateRequest_CopiesTheProviderAccountUnlessTheAgentChanges()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = "alice", Title = "Claude", Mode = SessionMode.Interactive,
            Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription, CredentialId = "work0001",
            AgentSessionId = "x", CallbackToken = "token"
        };

        Assert.Equal("work0001", SessionDuplication.CopyableRequest(source, new("Copy", null, false)).CredentialId);
        Assert.Equal("other002", SessionDuplication.CopyableRequest(source,
            new("Copy", null, false, CredentialId: "other002")).CredentialId);
        Assert.Null(SessionDuplication.CopyableRequest(source,
            new("Copy", null, false, Agent: AgentKind.Codex)).CredentialId);
        // Creation refuses an account on an API-key session, so a copy billed that way must not
        // inherit the pin — the dialog never showed the field, so a 400 about it would be baffling.
        Assert.Null(SessionDuplication.CopyableRequest(source,
            new("Copy", null, false, AuthMode: AgentAuthMode.ApiKey)).CredentialId);
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

        Assert.NotNull(copy.Policy);
        Assert.Equal(AgentKind.Codex, copy.Agent);
        Assert.Equal(AgentAuthMode.ApiKey, copy.AuthMode);
        Assert.Equal(["git status"], copy.Policy.AllowedCommands);
    }

    [Fact]
    public void DuplicateRequest_CopiesCursorAgentAuthAndPolicy()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = "alice", Title = "Cursor", Mode = SessionMode.Autonomous,
            Agent = AgentKind.Cursor, AuthMode = AgentAuthMode.ApiKey,
            AgentSessionId = "chat", CallbackToken = "token", Status = "Succeeded",
            AgentPolicyJson = "{\"allowedTools\":[\"Shell(git status)\"],\"allowedMcpTools\":[],\"allowedCommands\":[]}"
        };

        var copy = SessionDuplication.CopyableRequest(source, new("Copy", null, false));

        Assert.Equal(AgentKind.Cursor, copy.Agent);
        Assert.Equal(AgentAuthMode.ApiKey, copy.AuthMode);
        Assert.Equal(["Shell(git status)"], copy.Policy!.AllowedTools);
    }

    [Fact]
    public void DuplicateRequest_CopiesOpenClawApiKeySource()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = "alice", Title = "OpenClaw", Mode = SessionMode.Autonomous,
            Agent = AgentKind.OpenClaw, AuthMode = AgentAuthMode.ApiKey,
            OpenClawApiKeySource = OpenClawApiKeySource.Anthropic,
            AgentSessionId = "thread", CallbackToken = "token", Status = "Succeeded"
        };

        var copy = SessionDuplication.CopyableRequest(source, new("Copy", null, false));

        Assert.Equal(AgentKind.OpenClaw, copy.Agent);
        Assert.Equal(AgentAuthMode.ApiKey, copy.AuthMode);
        Assert.Equal(OpenClawApiKeySource.Anthropic, copy.OpenClawApiKeySource);
    }

    [Fact]
    public void DuplicateRequest_AppliesOpenClawApiKeySourceWhenSwitchingToOpenClawApiKey()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = "alice", Title = "Claude", Mode = SessionMode.Autonomous,
            Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription,
            AgentSessionId = "thread", CallbackToken = "token", Status = "Succeeded"
        };

        var copy = SessionDuplication.CopyableRequest(source,
            new("Copy", null, false, AgentKind.OpenClaw, AgentAuthMode.ApiKey, null,
                OpenClawApiKeySource.Anthropic));

        Assert.Equal(AgentKind.OpenClaw, copy.Agent);
        Assert.Equal(AgentAuthMode.ApiKey, copy.AuthMode);
        Assert.Equal(OpenClawApiKeySource.Anthropic, copy.OpenClawApiKeySource);
        AgentConfiguration.ValidateForDuplicatedSession(copy.Agent, copy.AuthMode, copy.OpenClawApiKeySource);
    }

    [Fact]
    public void DuplicateRequest_AppliesRequestOpenClawApiKeySourceOverride()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = "alice", Title = "OpenClaw", Mode = SessionMode.Autonomous,
            Agent = AgentKind.OpenClaw, AuthMode = AgentAuthMode.ApiKey,
            OpenClawApiKeySource = OpenClawApiKeySource.Anthropic,
            AgentSessionId = "thread", CallbackToken = "token", Status = "Succeeded"
        };

        var copy = SessionDuplication.CopyableRequest(source,
            new("Copy", null, false, AgentKind.OpenClaw, AgentAuthMode.ApiKey, null,
                OpenClawApiKeySource.OpenAI));

        Assert.Equal(AgentKind.OpenClaw, copy.Agent);
        Assert.Equal(AgentAuthMode.ApiKey, copy.AuthMode);
        Assert.Equal(OpenClawApiKeySource.OpenAI, copy.OpenClawApiKeySource);
        AgentConfiguration.ValidateForDuplicatedSession(copy.Agent, copy.AuthMode, copy.OpenClawApiKeySource);
    }

    [Fact]
    public void DuplicateRequest_ClearsOpenClawApiKeySourceWhenOverrideLeavesNonOpenClawApiKey()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = "alice", Title = "OpenClaw", Mode = SessionMode.Autonomous,
            Agent = AgentKind.OpenClaw, AuthMode = AgentAuthMode.ApiKey,
            OpenClawApiKeySource = OpenClawApiKeySource.Anthropic,
            AgentSessionId = "thread", CallbackToken = "token", Status = "Succeeded"
        };

        var copy = SessionDuplication.CopyableRequest(source,
            new("Copy", null, false, AgentKind.Claude, AgentAuthMode.Subscription));

        Assert.Equal(AgentKind.Claude, copy.Agent);
        Assert.Equal(AgentAuthMode.Subscription, copy.AuthMode);
        Assert.Null(copy.OpenClawApiKeySource);
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

        Assert.NotNull(copy.Policy);
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
        Assert.NotNull(copy.Policy);

        Assert.Empty(copy.Policy.AllowedTools);
        Assert.Empty(copy.AllowedTools);
    }

    [Fact]
    public void DuplicateRequest_CopiesUiMode()
    {
        var source = new SessionRecord
        {
            Id = "s", Owner = "alice", Title = "Chat", Mode = SessionMode.Interactive,
            UiMode = SessionUiMode.Chat, Agent = AgentKind.Claude, AuthMode = AgentAuthMode.Subscription,
            AgentSessionId = "thread", CallbackToken = "token", Status = "Succeeded"
        };

        var copy = SessionDuplication.CopyableRequest(source, new("Copy", null, false));

        Assert.Equal(SessionUiMode.Chat, copy.UiMode);
    }

    [Fact]
    public void UpdateRequest_HasNoUiModeFieldSoUpdatesIgnoreIt()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var request = System.Text.Json.JsonSerializer.Deserialize<UpdateSessionRequest>(
            "{\"title\":\"Renamed\",\"uiMode\":\"chat\"}", options);

        Assert.NotNull(request);
        Assert.Equal("Renamed", request!.Title);
        Assert.Null(typeof(UpdateSessionRequest).GetProperty("UiMode"));
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
