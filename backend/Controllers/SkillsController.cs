using System.Security.Claims;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Personal skill library (community feature). Skills are SKILL.md documents
/// (stored in S3 when configured) that get materialized into agent pods.
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

    public SkillsController(ISkillStore store, ILibraryAccess access, ILibraryShareStore shares)
    {
        _store = store;
        _access = access;
        _shares = shares;
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

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var owner = Owner;
        var record = await _access.GetSkillAsync(owner, id, ct);
        if (record is null) return NotFound();
        var content = await _store.GetContentAsync(record, ct) ?? "";
        return Ok(new SkillDetail(
            record.Id, record.Name, record.Description, record.Owner,
            record.Owner == owner, content, record.CreatedAt, record.UpdatedAt));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveSkillRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(ToInfo(await _store.CreateAsync(Owner, request, ct), Owner));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] SaveSkillRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(ToInfo(await _store.UpdateAsync(Owner, id, request, ct), Owner));
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
        return NoContent();
    }

    private static SkillInfo ToInfo(SkillRecord record, string owner) => new(
        record.Id,
        record.Name,
        record.Description,
        record.Owner,
        record.Owner == owner,
        record.CreatedAt,
        record.UpdatedAt);
}
