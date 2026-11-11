using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

public sealed record ShareLinkReadRequest(string Link, int? Offset = null, int? Max = null)
{
    // MVC trace logging formats action arguments with ToString, including record properties.
    public override string ToString() => "ShareLinkReadRequest { Link = [redacted] }";
}
public sealed record ShareLinkSendRequest(string Link, string Message)
{
    public override string ToString() => "ShareLinkSendRequest { Link = [redacted], Message = [redacted] }";
}

// POST bodies keep the bearer capability out of access-log URLs and route values.
[ApiController]
[AllowAnonymous]
[Route("api/remote/share-link")]
public sealed class RemoteShareLinkController(ExternalShareLinkService links) : ControllerBase
{
    [HttpPost("read")]
    public async Task<IActionResult> Read(ShareLinkReadRequest request, CancellationToken ct)
        => await links.ReadAsync(request.Link, request.Offset, request.Max, ct) is { } result
            ? Ok(result) : NotFound();

    [HttpPost("send")]
    public async Task<IActionResult> Send(ShareLinkSendRequest request, CancellationToken ct)
    {
        try
        {
            return await links.SendAsync(request.Link, request.Message, ct) is { } result
                ? Ok(result) : NotFound();
        }
        catch (ChatInboxUnsupportedException error) { return Conflict(new { code = ChatInboxUnsupportedException.Code, message = error.Message }); }
        catch (ArgumentException) { return BadRequest(new { error = "message_body_invalid" }); }
    }
}
