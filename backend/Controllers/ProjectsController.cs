using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace AgentHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/projects")]
public sealed class ProjectsController : ControllerBase
{
    private readonly IProjectStore _store;

    public ProjectsController(IProjectStore store) => _store = store;

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    [HttpGet]
    public async Task<IReadOnlyList<ProjectInfo>> List(CancellationToken ct)
        => await _store.ListAsync(Owner, ct);

    [HttpPost]
    public async Task<ActionResult<ProjectInfo>> Create([FromBody] CreateProjectRequest request, CancellationToken ct)
    {
        if (!ProjectValidation.IsValidName(request.Name) || !ProjectValidation.IsValidColor(request.Color))
            return BadRequest("Invalid project name or color.");

        try { return Ok(await _store.CreateAsync(Owner, request, ct)); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("A project with this name already exists.");
        }
    }

    [HttpPatch("{id}")]
    public async Task<ActionResult<ProjectInfo>> Update(string id, [FromBody] UpdateProjectRequest request, CancellationToken ct)
    {
        if (request.Name is not null && !ProjectValidation.IsValidName(request.Name) ||
            !ProjectValidation.IsValidColor(request.Color))
            return BadRequest("Invalid project name or color.");

        try
        {
            return await _store.UpdateAsync(Owner, id, request, ct) is { } project ? Ok(project) : NotFound();
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("A project with this name already exists.");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
        => await _store.DeleteAsync(Owner, id, ct) ? NoContent() : NotFound();
}
