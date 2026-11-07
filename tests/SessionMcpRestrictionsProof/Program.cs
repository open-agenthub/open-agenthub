using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Library;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Mvc;

// Synthetic callbacks use the production controller and matcher; no service credentials
// or external model endpoints are involved in this proof host.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapPost("/{agent}/agent-policy", async (string agent, InternalController.AgentPolicyBody body, HttpContext http) =>
{
    var session = new SessionRecord { Id = "proof", Owner = "owner", CallbackToken = "proof-token",
        Agent = Enum.Parse<AgentKind>(agent, true), Mode = SessionMode.Interactive };
    var controller = new InternalController(new CallbackSessionStore(session), [], null!, null!, [], [], new ProofPolicy(), null!);
    controller.ControllerContext = new ControllerContext { HttpContext = http };
    var result = await controller.AgentPolicy("proof", body, http.RequestAborted);
    return result is ObjectResult value ? Results.Json(value.Value) : Results.Unauthorized();
});
app.MapGet("/config", (string fixture, bool blocked = true) =>
{
    var inline = JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object>
    {
        ["probe-srv"] = new { command = "node", args = new[] { fixture, "blocked" } },
        ["allowed-srv"] = new { command = "node", args = new[] { fixture, "allowed" } }
    }});
    return Results.Text(McpConfigAssembler.Merge(inline, [], blockedServers: blocked ? ["probe-srv"] : [])!, "application/json");
});
app.MapGet("/token-proof", () =>
{
    var issued = new List<string>();
    var gateway = new McpGatewayAssembleOptions { BaseUrl = "https://example.com", SessionId = "proof",
        IssueToken = id => { issued.Add(id); return "synthetic"; },
        IssueEphemeralToken = name => { issued.Add(name); return "synthetic"; } };
    var config = McpConfigAssembler.Merge(null,
        [new McpServerRecord { Id = "catalog", Name = "blocked-catalog", Owner = "owner", Kind = "api", ConfigJson = "{}" }], gateway,
        [new EphemeralApiMcpEntry("proof", "blocked-api", "owner", "{}", null)], ["blocked-catalog", "blocked-api"]);
    return Results.Json(new { config, issued });
});
app.Run();

sealed class ProofPolicy : ISessionMcpPolicyReader
{
    public Task<SessionMcpPolicy?> GetMcpPolicyAsync(string id, CancellationToken ct = default) =>
        Task.FromResult<SessionMcpPolicy?>(new(["probe-srv"], ["mcp__allowed-srv__get-data.v2"], DateTime.UtcNow));
}

sealed class CallbackSessionStore(SessionRecord session) : ISessionStore
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
