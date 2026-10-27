using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

public sealed record SessionOrderRequest(List<string>? SessionIds);
/// <param name="StopActiveRun">Scheduled sessions only: stop a run in progress instead of
/// refusing the move while it lasts.</param>
public sealed record MoveSessionProjectRequest(string? ProjectId, bool Confirm = false, bool StopActiveRun = false);

[ApiController]
[Authorize]
[Route("api/session-organization")]
public sealed class SessionOrganizationController(PostgresSessionOrderStore orders, IProjectStore projects,
    ISessionStore records, ISessionService sessions) : ControllerBase
{
    private string Owner => User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();

    [HttpGet("order")]
    public Task<IReadOnlyList<string>> GetOrder(CancellationToken ct) => orders.GetAsync(Owner, ct);

    [HttpPut("order")]
    public async Task<IActionResult> SetOrder(SessionOrderRequest request, CancellationToken ct)
    {
        if (request.SessionIds is not { } ids || ids.Count > 10000 || ids.Any(string.IsNullOrWhiteSpace)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            return BadRequest("Supply unique session ids (at most 10000).");
        return await orders.SetAsync(Owner, ids, ct) ? NoContent() : NotFound();
    }

    [HttpPost("{id}/project")]
    public async Task<ActionResult<SessionInfo>> Move(string id, MoveSessionProjectRequest request, CancellationToken ct)
    {
        // Read the owned registry, not shared access: a collaborator cannot move another user's session.
        if (await records.GetAsync(Owner, id, ct) is not { } record) return NotFound();
        if (request.ProjectId is not null && await projects.GetAsync(Owner, request.ProjectId, ct) is null)
            return NotFound();
        if (!request.Confirm) return Conflict("Confirm the change in project secrets and project file access before moving.");
        try
        {
            return Ok(record.Mode == SessionMode.Scheduled
                ? await sessions.MoveScheduledSessionAsync(Owner, id, request.ProjectId, request.StopActiveRun, ct)
                : await sessions.UpdateSessionAsync(Owner, id, new UpdateSessionRequest { ProjectId = request.ProjectId }, ct));
        }
        catch (ScheduledRunActiveException error) { return Conflict(new { code = "scheduled_run_active", error = error.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException error) { return BadRequest(error.Message); }
        catch (InvalidOperationException error) { return Conflict(error.Message); }
    }
}
