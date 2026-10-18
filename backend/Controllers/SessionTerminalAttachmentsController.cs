using System.Security.Claims;
using System.Text.RegularExpressions;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

public sealed record TerminalAttachmentsRequest(List<string>? FileIds);

[ApiController]
[Authorize]
[Route("api/sessions/{id}/terminal-attachments")]
public sealed partial class SessionTerminalAttachmentsController(
    ISessionAccessService access, ISessionTerminalAttachments attachments) : ControllerBase
{
    // The browser pastes the answer into the agent's terminal, so this takes what typing there
    // takes, and what uploading the files took.
    [HttpPost]
    public async Task<IActionResult> Materialize(string id, [FromBody] TerminalAttachmentsRequest body, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var principal = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal is null) return Unauthorized();
        var resolved = await access.ResolveUserAsync(principal, id, ct);
        if (resolved is null || !SessionAccessRules.CanReadFiles(resolved.Level)) return NotFound();
        if (!SessionAccessRules.CanWriteTerminal(resolved.Level) || !SessionAccessRules.CanWriteFiles(resolved.Level)) return Forbid();
        var ids = body?.FileIds;
        if (ids is null || ids.Count is < 1 or > 5 || ids.Distinct().Count() != ids.Count || !ids.All(FileId().IsMatch))
            return BadRequest(new { error = "invalid_request" });
        var answer = await attachments.MaterializeAsync(resolved.Session, ids, ct);
        if (answer is not { } pod) return StatusCode(503, new { error = "attachments_unavailable" });
        // 404 is a runtime from before the route existed; the other refusals keep their meaning.
        return pod.Status switch
        {
            200 => Content(pod.Body, "application/json"),
            404 => StatusCode(501, new { error = "attachments_unsupported" }),
            400 or 409 or 413 or 422 => new ContentResult { StatusCode = pod.Status, Content = pod.Body, ContentType = "application/json" },
            _ => StatusCode(502, new { error = "attachment_delivery_failed" })
        };
    }

    [GeneratedRegex("^[a-f0-9]{32}$")]
    private static partial Regex FileId();
}
