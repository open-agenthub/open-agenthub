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

    private static SessionRecord Session(SessionMode mode) => new()
    {
        Id = "session-1",
        Owner = "alice",
        CallbackToken = "callback-token",
        Mode = mode,
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
            new CallbackSessionStore(session), [], null!, null!, null!, sharing!);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.Request.Headers["X-Agent-Token"] = token;
        return controller;
    }

    private sealed class FakeMcpPolicyReader(SessionMcpPolicy? policy) : ISessionMcpPolicyReader
    {
        public Task<SessionMcpPolicy?> GetMcpPolicyAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(policy);
    }

    private sealed class CallbackSessionStore(SessionRecord session) : ISessionStore
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
