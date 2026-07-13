using System.Security.Claims;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Read-only token/cost usage for the signed-in user. Data is fed by the agent pods'
/// OpenTelemetry exporter via <see cref="OtelController"/>; here it is only aggregated
/// and returned, always scoped to the caller's owner.
/// </summary>
[ApiController]
[Authorize]
[Route("api/usage")]
public sealed class UsageController : ControllerBase
{
    private readonly IUsageStore _usage;
    public UsageController(IUsageStore usage) => _usage = usage;

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    /// <summary>Per-session usage rows for the owner (most recently active first).</summary>
    [HttpGet("sessions")]
    public async Task<IReadOnlyList<SessionUsage>> Sessions(CancellationToken ct)
        => await _usage.ListByOwnerAsync(Owner, ct);

    [HttpGet("sessions/{id}")]
    public async Task<ActionResult<SessionUsage>> Session(string id, CancellationToken ct)
        => await _usage.GetAsync(Owner, id, ct) is { } u ? Ok(u) : NotFound();

    /// <summary>
    /// Owner totals. Optional <c>from</c>/<c>to</c> (ISO-8601) restrict to sessions last active
    /// in the window.
    /// </summary>
    [HttpGet("summary")]
    public async Task<UsageSummary> Summary([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        => await _usage.SummaryAsync(Owner,
            from?.ToUniversalTime(), to?.ToUniversalTime(), ct);
}
