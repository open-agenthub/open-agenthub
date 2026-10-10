using System.Security.Claims;
using AgentHub.Api.Browser;
using AgentHub.Api.Ee.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/sessions/{id}/browser/clipboard")]
public sealed class BrowserClipboardController(
    ISessionAccessService access,
    IBrowserService browsers,
    IBrowserRuntimeClient runtime) : ControllerBase
{
    private string Principal =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    [HttpPost("paste")]
    [RequestSizeLimit(BrowserClipboardApi.MaxRequestBytes)]
    public async Task<IActionResult> Paste(
        string id, [FromBody] BrowserClipboardPasteRequest request, CancellationToken ct) =>
        await BrowserClipboardApi.PasteAsync(this,
            await access.ResolveUserAsync(Principal, id, ct), request, browsers, runtime, ct);

    [HttpPost("copy")]
    public async Task<IActionResult> Copy(
        string id, [FromBody] BrowserClipboardCopyRequest request, CancellationToken ct) =>
        await BrowserClipboardApi.CopyAsync(this,
            await access.ResolveUserAsync(Principal, id, ct), request, browsers, runtime, ct);
}
