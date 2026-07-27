using System.Security.Claims;
using AgentHub.Api.Persistence;
using AgentHub.Api.Usage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Token/cost usage for the signed-in user. Data is fed by the agent pods' OpenTelemetry
/// exporter via <see cref="OtelController"/>; here it is only aggregated and returned,
/// always scoped to the caller's owner. Also exposes the caller's personal monthly API
/// budget (community feature).
/// </summary>
[ApiController]
[Authorize]
[Route("api/usage")]
public sealed class UsageController : ControllerBase
{
    private readonly IUsageStore _usage;
    private readonly UsageLimitService _limits;
    private readonly UserDirectory _users;

    public UsageController(IUsageStore usage, UsageLimitService limits, UserDirectory users)
    {
        _usage = usage;
        _limits = limits;
        _users = users;
    }

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

    /// <summary>The caller's limit situation: personal + effective limit, month-to-date spend.</summary>
    [HttpGet("limit")]
    public async Task<UsageLimitStatus> Limit(CancellationToken ct)
        => await _limits.GetStatusAsync(Owner, ct);

    public sealed record SetLimitBody(double? LimitUsd);

    /// <summary>Sets (or clears, with null) the caller's own monthly API budget.</summary>
    [HttpPut("limit")]
    public async Task<ActionResult<UsageLimitStatus>> SetLimit([FromBody] SetLimitBody body, CancellationToken ct)
    {
        if (body.LimitUsd is { } l && (double.IsNaN(l) || double.IsInfinity(l) || l < 0))
            return BadRequest("limitUsd must be a non-negative number.");
        await _users.SetUsageLimitAsync(Owner, body.LimitUsd, ct);
        return Ok(await _limits.GetStatusAsync(Owner, ct));
    }
}
