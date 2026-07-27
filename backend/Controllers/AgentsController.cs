using AgentHub.Api.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Public (authenticated) agent metadata for UI filtering — not the admin allowlist editor.
/// </summary>
[ApiController]
[Authorize]
[Route("api/agents")]
public sealed class AgentsController : ControllerBase
{
    private readonly IAllowedAgentsProvider _allowed;

    public AgentsController(IAllowedAgentsProvider allowed) => _allowed = allowed;

    public sealed record AllowedAgentsResponse(IReadOnlyList<string> Agents);

    /// <summary>Current allowed agent kinds for session selectors.</summary>
    [HttpGet("allowed")]
    public async Task<IActionResult> GetAllowed(CancellationToken ct)
    {
        var agents = await _allowed.GetAllowedAsync(ct);
        return Ok(new AllowedAgentsResponse(agents.Select(a => a.ToString()).ToArray()));
    }
}
