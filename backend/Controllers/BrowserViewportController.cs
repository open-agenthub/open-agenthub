using System.Security.Claims;
using AgentHub.Api.Browser;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

public sealed record BrowserViewportRequest(int Width, int Height);

[ApiController]
[Authorize]
[Route("api/sessions/{id}/browser/viewport")]
public sealed class BrowserViewportController(
    ISessionStore sessions,
    IBrowserService browsers) : ControllerBase
{
    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    [HttpPut]
    public async Task<IActionResult> Resize(
        string id, [FromBody] BrowserViewportRequest request, CancellationToken ct)
    {
        if (await sessions.GetAsync(Owner, id, ct) is null)
            return NotFound();
        if (!BrowserViewport.TryCreate(request.Width, request.Height, out var viewport))
            return BadRequest("Viewport dimensions are outside the supported range.");

        try
        {
            await browsers.ResizeAsync(id, viewport!, ct);
            return NoContent();
        }
        catch (InvalidOperationException e)
        {
            return Conflict(e.Message);
        }
        catch (HttpRequestException e)
        {
            return StatusCode(StatusCodes.Status502BadGateway, e.Message);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            return StatusCode(StatusCodes.Status502BadGateway, e.Message);
        }
    }
}
