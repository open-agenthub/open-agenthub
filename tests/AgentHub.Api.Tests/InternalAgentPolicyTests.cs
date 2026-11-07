using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public class InternalAgentPolicyTests
{
    [Fact]
    public async Task AgentPolicy_AuthenticatesCallbackTokenAndSessionId()
    {
        var controller = Controller(Session(SessionMode.Autonomous), null, "wrong-token");

        var result = await controller.AgentPolicy("session-1", Body("Read"), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
    }

    [Fact]
    public async Task AgentPolicy_AppliesSharingDenyBeforeInteractiveAsk()
    {
        var sharing = new FakeMcpPolicyReader(new SessionMcpPolicy([], ["mcp__docs__search"], DateTime.UtcNow));
        var controller = Controller(Session(SessionMode.Interactive), sharing);

        var result = await controller.AgentPolicy("session-1", Body("mcp__docs__search"), CancellationToken.None);

        Assert.Equal("deny", Decision(result));
    }

    /// <summary>#504: an unattended Codex session with auto-approve off and an allow list that
    /// named container_run started a helper and was refused container_stop and its own inbox.
    /// The self-service built-ins need no entry; capability-extending ones still do, and an
    /// owner's sharing block still wins.</summary>
    [Theory]
    [InlineData("mcp__agenthub_sessions__container_stop", "allow")]
    [InlineData("mcp__agenthub_sessions__agent_inbox", "allow")]
    [InlineData("mcp__agenthub_network__port_list", "allow")]
    [InlineData("mcp__agenthub_sessions__container_run", "allow")]
    [InlineData("mcp__agenthub_sessions__session_create", "deny")]
    [InlineData("mcp__agenthub_sessions__secret_request", "deny")]
    [InlineData("mcp__agenthub_network__port_request", "deny")]
    public async Task AgentPolicy_SelfServiceBuiltInsNeedNoAllowListEntry(string tool, string expected)
    {
        var policy = new AgentPolicy { AllowedMcpTools = ["mcp__agenthub_sessions__container_run"] };
        var session = Session(SessionMode.Autonomous);
        session.AgentPolicyJson = JsonSerializer.Serialize(policy);
        var controller = Controller(session, new FakeMcpPolicyReader(null));

        Assert.Equal(expected, Decision(await controller.AgentPolicy("session-1", Body(tool), CancellationToken.None)));
    }

    [Fact]
    public async Task AgentPolicy_TheOwnersSharingBlockStillAppliesToSelfServiceBuiltIns()
    {
        var sharing = new FakeMcpPolicyReader(new SessionMcpPolicy([], ["mcp__agenthub_sessions__agent_inbox"], DateTime.UtcNow));
        var controller = Controller(Session(SessionMode.Autonomous), sharing);

        Assert.Equal("deny", Decision(await controller.AgentPolicy(
            "session-1", Body("mcp__agenthub_sessions__agent_inbox"), CancellationToken.None)));
        Assert.Equal("allow", Decision(await controller.AgentPolicy(
            "session-1", Body("mcp__agenthub_sessions__container_stop"), CancellationToken.None)));
    }

    [Fact]
    public async Task AgentPolicy_InteractivePreservesNormalApprovalFlow()
    {
        var controller = Controller(Session(SessionMode.Interactive), new FakeMcpPolicyReader(null));

        var result = await controller.AgentPolicy("session-1", Body("Read"), CancellationToken.None);

        Assert.Equal("ask", Decision(result));
    }

    [Theory]
    [InlineData(SessionMode.Autonomous)]
    [InlineData(SessionMode.Scheduled)]
    public async Task AgentPolicy_NonInteractiveUsesPersistedDefaultDenyPolicy(SessionMode mode)
    {
        var controller = Controller(Session(mode), new FakeMcpPolicyReader(null));

        Assert.Equal("allow", Decision(await controller.AgentPolicy(
            "session-1", Body("Bash", new { command = "git status --short" }), CancellationToken.None)));
        Assert.Equal("deny", Decision(await controller.AgentPolicy(
            "session-1", Body("Bash", new { command = "git push" }), CancellationToken.None)));
    }

    // The flag has to be read here, not only at /permission: the runtime hook asks this endpoint
    // first, and a "deny" settles the call before the approval endpoint is ever reached. That is
    // what made auto-approve do nothing for an unattended Codex session.
    [Theory]
    [InlineData(SessionMode.Autonomous)]
    [InlineData(SessionMode.Scheduled)]
    public async Task AgentPolicy_AutoApproveLeavesUncoveredCallsToTheApprovalEndpoint(SessionMode mode)
    {
        var controller = Controller(Session(mode, autoApprove: true), new FakeMcpPolicyReader(null));

        Assert.Equal("allow", Decision(await controller.AgentPolicy(
            "session-1", Body("Read"), CancellationToken.None)));
        Assert.Equal("ask", Decision(await controller.AgentPolicy(
            "session-1", Body("WebFetch"), CancellationToken.None)));
        Assert.Equal("ask", Decision(await controller.AgentPolicy(
            "session-1", Body("Bash", new { command = "git push" }), CancellationToken.None)));
    }

    // #244: the smoke command an autonomous Codex session was refused. Interactive stays "ask" (a
    // person decides), an unattended session without auto-approve stays "deny", and only an
    // auto-approving one reaches the approval endpoint.
    [Theory]
    [InlineData(SessionMode.Interactive, false, "ask")]
    [InlineData(SessionMode.Interactive, true, "ask")]
    [InlineData(SessionMode.Autonomous, false, "deny")]
    [InlineData(SessionMode.Scheduled, false, "deny")]
    [InlineData(SessionMode.Autonomous, true, "ask")]
    [InlineData(SessionMode.Scheduled, true, "ask")]
    public async Task AgentPolicy_ARedirectedCommandFollowsTheSessionsApprovalMode(
        SessionMode mode, bool autoApprove, string expected)
    {
        var controller = Controller(Session(mode, autoApprove), new FakeMcpPolicyReader(null));

        Assert.Equal(expected, Decision(await controller.AgentPolicy("session-1",
            Body("Bash", new { command = "echo smoke > smoke.txt && cat smoke.txt && uname -m" }),
            CancellationToken.None)));
    }

    [Fact]
    public async Task AgentPolicy_AutoApproveDoesNotOverrideTheSharingDeny()
    {
        var sharing = new FakeMcpPolicyReader(new SessionMcpPolicy([], ["mcp__docs__search"], DateTime.UtcNow));
        var controller = Controller(Session(SessionMode.Autonomous, autoApprove: true), sharing);

        var result = await controller.AgentPolicy("session-1", Body("mcp__docs__search"), CancellationToken.None);

        Assert.Equal("deny", Decision(result));
    }

    [Fact]
    public async Task AgentPolicy_MalformedPersistedPolicyFailsClosed()
    {
        var session = Session(SessionMode.Autonomous);
        session.AgentPolicyJson = "{malformed";
        var controller = Controller(session, new FakeMcpPolicyReader(null));

        var result = await controller.AgentPolicy("session-1", Body("Read"), CancellationToken.None);

        Assert.Equal("deny", Decision(result));
    }

    private static InternalController.AgentPolicyBody Body(string tool, object? input = null) =>
        new(tool, JsonSerializer.SerializeToElement(input ?? new { }));

    private static string Decision(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        return json.RootElement.GetProperty("decision").GetString()!;
    }

    private static SessionRecord Session(SessionMode mode, bool autoApprove = false) => new()
    {
        Id = "session-1",
        Owner = "alice",
        CallbackToken = "callback-token",
        Mode = mode,
        AutoApprove = autoApprove,
        Agent = AgentKind.Codex,
        AuthMode = AgentAuthMode.ApiKey,
        AgentPolicyJson = "{\"allowedTools\":[\"Read\"],\"allowedMcpTools\":[],\"allowedCommands\":[\"git status\"]}"
    };

    private static InternalController Controller(
        SessionRecord session,
        ISessionMcpPolicyReader? sharing,
        string token = "callback-token")
    {
        var controller = new InternalController(
            new CallbackSessionStore(session), [], null!, null!, [], [], sharing!, null!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.Headers["X-Agent-Token"] = token;
        return controller;
    }

    private sealed class FakeMcpPolicyReader(SessionMcpPolicy? policy) : ISessionMcpPolicyReader
    {
        public Task<SessionMcpPolicy?> GetMcpPolicyAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(policy);
    }

    internal sealed class CallbackSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
