using System.Security.Claims;
using AgentHub.Api.Admin;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Org MCP catalog (<c>owner=__org__</c>). Admin-only; not license-gated.
/// Returns ConfigJson to admins (Mine=true for org entries in this controller).
/// </summary>
[ApiController]
[Authorize]
[Route("api/admin/mcp-servers")]
public sealed class AdminMcpServersController : ControllerBase
{
    private readonly IMcpServerStore _store;
    private readonly ILibraryShareStore _shares;
    private readonly AdminAccess _access;

    public AdminMcpServersController(
        IMcpServerStore store, ILibraryShareStore shares, AdminAccess access)
    {
        _store = store;
        _shares = shares;
        _access = access;
    }

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    private async Task<IActionResult?> GateAsync(CancellationToken ct)
        => await _access.IsAdminAsync(Owner, ct) ? null : Forbid();

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (await GateAsync(ct) is { } fail) return fail;
        var records = await _store.ListByOwnerAsync(McpServerRecord.OrgOwner, ct);
        return Ok(records.Select(r => McpServersController.ToInfo(r, mine: true)).ToList());
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveMcpServerRequest request, CancellationToken ct)
    {
        if (await GateAsync(ct) is { } fail) return fail;
        try
        {
            return Ok(McpServersController.ToInfo(
                await _store.CreateAsync(McpServerRecord.OrgOwner, request, ct), mine: true));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(
        string id, [FromBody] SaveMcpServerRequest request, CancellationToken ct)
    {
        if (await GateAsync(ct) is { } fail) return fail;
        try
        {
            return Ok(McpServersController.ToInfo(
                await _store.UpdateAsync(McpServerRecord.OrgOwner, id, request, ct), mine: true));
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
        if (await GateAsync(ct) is { } fail) return fail;
        try
        {
            await _store.DeleteAsync(McpServerRecord.OrgOwner, id, ct);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        await _shares.DeleteForItemAsync(LibraryItemTypes.Mcp, id, ct);
        return NoContent();
    }
}
