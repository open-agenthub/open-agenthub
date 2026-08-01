using System.Security.Claims;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Personal MCP server library (community feature). Users manage their own
/// entries; entries shared by others (enterprise) appear read-only in the list
/// and never expose their raw config to non-owners.
/// </summary>
[ApiController]
[Authorize]
[Route("api/mcp-servers")]
public sealed class McpServersController : ControllerBase
{
    private readonly IMcpServerStore _store;
    private readonly ILibraryAccess _access;
    private readonly ILibraryShareStore _shares;

    public McpServersController(IMcpServerStore store, ILibraryAccess access, ILibraryShareStore shares)
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
    public async Task<IReadOnlyList<McpServerInfo>> List(CancellationToken ct)
    {
        var owner = Owner;
        var records = await _access.ListMcpServersAsync(owner, ct);
        return records.Select(r => ToInfo(r, owner)).ToList();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveMcpServerRequest request, CancellationToken ct)
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
    public async Task<IActionResult> Update(string id, [FromBody] SaveMcpServerRequest request, CancellationToken ct)
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
        // Sharing rows are metadata of the deleted item — clean them up regardless
        // of the license state.
        await _shares.DeleteForItemAsync(LibraryItemTypes.Mcp, id, ct);
        return NoContent();
    }

    private static McpServerInfo ToInfo(McpServerRecord record, string owner)
    {
        var mine = record.Owner == owner;
        return new McpServerInfo(
            record.Id,
            record.Name,
            record.Description,
            record.Owner,
            mine,
            mine ? record.ConfigJson : null,
            record.CreatedAt,
            record.UpdatedAt);
    }
}
