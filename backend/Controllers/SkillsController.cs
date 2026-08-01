using System.Security.Claims;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Skill library (community feature). Skills are SKILL.md documents (stored in
/// S3 when configured) that get materialized into agent pods — personal or
/// scoped to one of the owner's projects, versioned on every save, searchable.
/// Sharing between users is an enterprise feature.
/// </summary>
[ApiController]
[Authorize]
[Route("api/skills")]
public sealed class SkillsController : ControllerBase
{
    private readonly ISkillStore _store;
    private readonly ILibraryAccess _access;
    private readonly ILibraryShareStore _shares;
    private readonly SkillSearchService _search;
    private readonly IProjectStore _projects;

    public SkillsController(ISkillStore store, ILibraryAccess access, ILibraryShareStore shares,
        SkillSearchService search, IProjectStore projects)
    {
        _store = store;
        _access = access;
        _shares = shares;
        _search = search;
        _projects = projects;
    }

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    [HttpGet]
    public async Task<IReadOnlyList<SkillInfo>> List(CancellationToken ct)
    {
        var owner = Owner;
        var records = await _access.ListSkillsAsync(owner, ct);
        return records.Select(r => ToInfo(r, owner)).ToList();
    }

    /// <summary>Hybrid search across everything the user can see; optional project filter
    /// ("" = personal skills only, omitted = no filter).</summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] string q, [FromQuery] string? projectId,
        [FromQuery] int limit = SkillSearchService.DefaultLimit, CancellationToken ct = default)
    {
        var owner = Owner;
        if (string.IsNullOrWhiteSpace(q))
            return Ok(Array.Empty<SkillSearchHit>());
        IReadOnlyList<SkillRecord> accessible = await _access.ListSkillsAsync(owner, ct);
        if (projectId is not null)
        {
            accessible = accessible
                .Where(r => r.Owner != owner || (r.ProjectId ?? "") == projectId)
                .ToList();
        }
        return Ok(await _search.SearchAsync(owner, accessible, q, limit, ct));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var owner = Owner;
        var record = await _access.GetSkillAsync(owner, id, ct);
        if (record is null) return NotFound();
        var content = await _store.GetContentAsync(record, ct) ?? "";
        return Ok(new SkillDetail(
            record.Id, record.Name, record.Description, record.Owner,
            record.Owner == owner, record.ProjectId, record.Version, content,
            record.CreatedAt, record.UpdatedAt));
    }

    [HttpGet("{id}/versions")]
    public async Task<IActionResult> Versions(string id, CancellationToken ct)
    {
        var record = await _access.GetSkillAsync(Owner, id, ct);
        if (record is null) return NotFound();
        var versions = await _store.ListVersionsAsync(id, ct);
        return Ok(versions
            .Select(v => new SkillVersionInfo(
                v.Version, v.Name, v.Description, v.CreatedBy, v.Comment, v.CreatedAt))
            .ToList());
    }

    [HttpGet("{id}/versions/{version:int}")]
    public async Task<IActionResult> VersionContent(string id, int version, CancellationToken ct)
    {
        var owner = Owner;
        var record = await _access.GetSkillAsync(owner, id, ct);
        if (record is null) return NotFound();
        var content = await _store.GetVersionContentAsync(id, version, ct);
        if (content is null) return NotFound();
        return Ok(new SkillDetail(
            record.Id, record.Name, record.Description, record.Owner,
            record.Owner == owner, record.ProjectId, version, content,
            record.CreatedAt, record.UpdatedAt));
    }

    public sealed record RestoreRequest(int Version);

    /// <summary>Re-publishes an old version as a new head version (own skills only).</summary>
    [HttpPost("{id}/restore")]
    public async Task<IActionResult> Restore(string id, [FromBody] RestoreRequest request, CancellationToken ct)
    {
        var owner = Owner;
        try
        {
            var record = await _store.RestoreVersionAsync(owner, id, request.Version, owner, ct);
            await IndexAsync(record, ct);
            return Ok(ToInfo(record, owner));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveSkillRequest request, CancellationToken ct)
    {
        var owner = Owner;
        try
        {
            var sanitized = await SanitizeAsync(owner, request, ct);
            var record = await _store.CreateAsync(owner, sanitized, ct);
            await _search.IndexAsync(record, sanitized.Content, ct);
            return Ok(ToInfo(record, owner));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] SaveSkillRequest request, CancellationToken ct)
    {
        var owner = Owner;
        try
        {
            // The project a skill lives in is fixed at creation; updates only touch content.
            var sanitized = await SanitizeAsync(owner, request with { ProjectId = null }, ct);
            var record = await _store.UpdateAsync(owner, id, sanitized, ct);
            await _search.IndexAsync(record, sanitized.Content, ct);
            return Ok(ToInfo(record, owner));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        try
        {
            await _store.DeleteAsync(Owner, id, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        await _shares.DeleteForItemAsync(LibraryItemTypes.Skill, id, ct);
        await _search.RemoveAsync(id, ct);
        return NoContent();
    }

    /// <summary>Saves through the API are always attributed to the user; a target
    /// project must be one of the owner's projects.</summary>
    private async Task<SaveSkillRequest> SanitizeAsync(
        string owner, SaveSkillRequest request, CancellationToken ct)
    {
        var projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? null : request.ProjectId;
        if (projectId is not null && await _projects.GetAsync(owner, projectId, ct) is null)
            throw new ArgumentException("Project not found.");
        return request with { ProjectId = projectId, SavedBy = owner };
    }

    private async Task IndexAsync(SkillRecord record, CancellationToken ct)
    {
        var content = await _store.GetContentAsync(record, ct);
        if (!string.IsNullOrEmpty(content))
            await _search.IndexAsync(record, content, ct);
    }

    private static SkillInfo ToInfo(SkillRecord record, string owner) => new(
        record.Id,
        record.Name,
        record.Description,
        record.Owner,
        record.Owner == owner,
        record.ProjectId,
        record.Version,
        record.CreatedAt,
        record.UpdatedAt);
}
