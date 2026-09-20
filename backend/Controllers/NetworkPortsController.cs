using AgentHub.Api.Files;
using AgentHub.Api.Network;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Runtime network port requests, called ONLY by the agent pod (via the built-in
/// agenthub_network MCP server). No user auth; the per-session callback token
/// (header X-Agent-Token) authorizes the caller, like the other /internal endpoints.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("internal/sessions/{id}/network")]
public sealed class NetworkPortsController(
    IAgentCallbackAuthorizer authorizer,
    NetworkPortService network) : ControllerBase
{
    public record PortRequestBody(string Direction, int Port, string? Protocol, string? Reason);

    private Task<SessionRecord?> AuthAsync(string id, CancellationToken ct) =>
        authorizer.AuthorizeAsync(Request, id, ct);

    /// <summary>
    /// Asks for an extra port. Outside the allowlist → immediate deny (nobody is asked).
    /// Already granted / auto-approved → immediate allow. Otherwise a pending request is
    /// created on the standard permission channel and its id returned for polling.
    /// </summary>
    [HttpPost("port-requests")]
    public async Task<IActionResult> RequestPort(string id, [FromBody] PortRequestBody body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        if (!PortRequestKey.TryParseDirection(body.Direction, out var direction))
            return BadRequest("direction must be 'egress' or 'browser_to_agent'.");
        if (body.Port is < 1 or > 65535)
            return BadRequest("port must be between 1 and 65535.");
        var protocol = (body.Protocol ?? "TCP").ToUpperInvariant();
        if (protocol is not ("TCP" or "UDP"))
            return BadRequest("protocol must be TCP or UDP.");
        // The browser talks HTTP/WebSocket to the agent's dev server — TCP only.
        if (direction == PortDirection.BrowserToAgent && protocol != "TCP")
            return BadRequest("browser_to_agent supports TCP only.");

        var outcome = await network.RequestAsync(rec, new PortRequestInput(direction, body.Port, protocol, body.Reason), ct);
        return outcome.RequestId is not null
            ? Ok(new { id = outcome.RequestId })
            : Ok(new { decision = outcome.Decision, reason = outcome.Reason });
    }

    /// <summary>Polled by the agent: "allow" | "deny" | "expired" | "pending".
    /// The first poll that sees an approval applies the NetworkPolicies.</summary>
    [HttpGet("port-requests/{reqId}")]
    public async Task<IActionResult> PortRequestStatus(string id, string reqId, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        return Ok(new { decision = await network.GetDecisionAsync(rec, reqId, ct) });
    }

    /// <summary>The agent gave up waiting; expires the request and defuses chat prompts.
    /// Returns the final decision — a click that won the race is still honored.</summary>
    [HttpPost("port-requests/{reqId}/expire")]
    public async Task<IActionResult> ExpirePortRequest(string id, string reqId, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        return Ok(new { decision = await network.ExpireAsync(rec, reqId, ct) });
    }

    /// <summary>Active port grants of this session.</summary>
    [HttpGet("ports")]
    public async Task<IActionResult> ListPorts(string id, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();
        var grants = await network.ListGrantsAsync(rec.Id, ct);
        return Ok(new
        {
            ports = grants.Select(g => new
            {
                direction = PortRequestKey.WireName(g.Direction),
                port = g.Port,
                protocol = g.Protocol,
                createdAt = g.CreatedAt
            })
        });
    }
}
