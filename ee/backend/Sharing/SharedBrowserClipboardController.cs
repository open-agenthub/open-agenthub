// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — shared browser clipboard.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Browser;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Ee.Sharing;

// A share link can grant control of the browser (Collaborator), so it gets the clipboard too;
// a view-only link is refused by the same rule that keeps its VNC connection read-only.
[ApiController]
[AllowAnonymous]
[Route("api/shared/{token}/browser/clipboard")]
public sealed class SharedBrowserClipboardController(
    ISessionAccessService access,
    IBrowserService browsers,
    IBrowserRuntimeClient runtime) : ControllerBase
{
    [HttpPost("paste")]
    [RequestSizeLimit(BrowserClipboardApi.MaxRequestBytes)]
    public async Task<IActionResult> Paste(
        string token, [FromBody] BrowserClipboardPasteRequest request, CancellationToken ct) =>
        await BrowserClipboardApi.PasteAsync(this,
            await access.ResolveTokenReadOnlyAsync(token, ct), request, browsers, runtime, ct);

    [HttpPost("copy")]
    public async Task<IActionResult> Copy(
        string token, [FromBody] BrowserClipboardCopyRequest request, CancellationToken ct) =>
        await BrowserClipboardApi.CopyAsync(this,
            await access.ResolveTokenReadOnlyAsync(token, ct), request, browsers, runtime, ct);
}
