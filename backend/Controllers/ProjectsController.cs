using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
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
    private readonly ISessionService? _sessions;

    /// <remarks>The session service is optional only for the tests that exercise names and colors;
    /// in the app it is always registered, and a secret selection without it is refused.</remarks>
    public ProjectsController(IProjectStore store, ISessionService? sessions = null)
    {
        _store = store;
        _sessions = sessions;
    }

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

        try
        {
            request = request with { SecretIds = await NormalizeSecretIdsAsync(request.SecretIds, ct) };
            return Ok(await _store.CreateAsync(Owner, request, ct));
        }
        catch (ArgumentException e) { return BadRequest(e.Message); }
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
            request = request with { SecretIds = await NormalizeSecretIdsAsync(request.SecretIds, ct) };
            if (await _store.UpdateAsync(Owner, id, request, ct) is not { } project) return NotFound();
            // A narrowed project takes the secret away from its running sessions too, not only
            // from the ones started after the change.
            if (request.SecretIds is not null && _sessions is not null)
                await _sessions.SyncSessionSecretsAsync(Owner, ct);
            return Ok(project);
        }
        catch (ArgumentException e) { return BadRequest(e.Message); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Conflict("A project with this name already exists.");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (!await _store.DeleteAsync(Owner, id, ct)) return NotFound();
        // Its sessions are now outside any project, where following the project means nothing.
        if (_sessions is not null) await _sessions.SyncSessionSecretsAsync(Owner, ct);
        return NoContent();
    }

    private async Task<List<string>?> NormalizeSecretIdsAsync(List<string>? requested, CancellationToken ct)
    {
        if (requested is null) return null;
        if (requested.Count == 0) return [];
        if (_sessions is null) throw new ArgumentException("Secrets are not available here.");
        var stored = (await _sessions.ListSessionSecretsAsync(Owner, ct)).Select(s => s.Id).ToList();
        return SessionSecretSelection.NormalizeProject(requested, stored).ToList();
    }
}
