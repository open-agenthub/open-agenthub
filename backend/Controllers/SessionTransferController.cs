using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHub.Api.Controllers;

public sealed record TakeLocalRequest(string? Directory, bool Confirm = false, bool Link = false);
public sealed record HandBackLinkRequest(string? Directory, bool Resume = true);

/// <summary>
/// Taking a session's conversation to a workstation and handing it back
/// (docs/session-transfer.md), on three surfaces:
/// <list type="bullet">
/// <item><c>api/remote/…/transfer</c> — personal API token; the stdio MCP server writes the files itself.</item>
/// <item><c>api/sessions/…/transfer</c> — the web app's login; returns single-use links and commands.</item>
/// <item><c>api/session-transfer/{secret}</c> — the link those commands use, with no other credential.</item>
/// </list>
/// The rules live in <see cref="SessionTransferService"/>; this class only authenticates and maps codes.
/// [AllowAnonymous] sits on each token and link action, not on the class: on the class it would
/// override the [Authorize] of the web app's actions and leave them open.
/// </summary>
[ApiController]
public sealed class SessionTransferController : ControllerBase
{
    private readonly Func<string, CancellationToken, Task<RemoteCaller?>> _findCaller;
    private readonly SessionTransferService _transfer;

    [ActivatorUtilitiesConstructor]
    public SessionTransferController(ApiTokenStore tokens, SessionTransferService transfer)
        : this(tokens.FindCallerByTokenAsync, transfer) { }

    /// <summary>Test seam: resolve the token caller without Postgres.</summary>
    public SessionTransferController(Func<string, CancellationToken, Task<RemoteCaller?>> findCaller, SessionTransferService transfer)
    {
        _findCaller = findCaller;
        _transfer = transfer;
    }

    // ------------------------------------------------------------------ personal API token

    [AllowAnonymous]
    [HttpGet("api/remote/sessions/{id}/transfer")]
    public Task<IActionResult> RemoteStatus(string id, CancellationToken ct)
        => AsToken(owner => _transfer.StatusAsync(owner, id, ct), ct);

    [AllowAnonymous]
    [HttpPost("api/remote/sessions/{id}/transfer/take-local")]
    public Task<IActionResult> RemoteTakeLocal(string id, [FromBody] TakeLocalRequest body, CancellationToken ct)
        => AsToken(owner => _transfer.TakeLocalAsync(owner, id, body.Directory, body.Confirm, body.Link, ct), ct);

    [AllowAnonymous]
    [HttpGet("api/remote/sessions/{id}/transfer/local-archive")]
    public async Task<IActionResult> RemoteLocalArchive(string id, [FromQuery] string? directory, CancellationToken ct)
    {
        var owner = await TokenOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        return await Map(async () => Archive(await _transfer.LocalArchiveAsync(owner, id, directory, ct), id));
    }

    [AllowAnonymous]
    [HttpPut("api/remote/sessions/{id}/transfer/hand-back")]
    [RequestSizeLimit(SessionStateTransfer.MaxArchiveBytes)]
    public Task<IActionResult> RemoteHandBack(string id, [FromQuery] bool resume = true, CancellationToken ct = default)
        => AsToken(owner => _transfer.HandBackAsync(owner, id, Request.Body, resume, ct), ct);

    [AllowAnonymous]
    [HttpPost("api/remote/sessions/{id}/transfer/hand-back-link")]
    public Task<IActionResult> RemoteHandBackLink(string id, [FromBody] HandBackLinkRequest body, CancellationToken ct)
        => AsToken(owner => _transfer.HandBackLinkAsync(owner, id, body.Directory, body.Resume, ct), ct);

    // ------------------------------------------------------------------ web app login

    [Authorize]
    [HttpGet("api/sessions/{id}/transfer")]
    public Task<IActionResult> Status(string id, CancellationToken ct)
        => Map(async () => Ok(await _transfer.StatusAsync(LoginOwner, id, ct)));

    /// <summary>The web app always gets a link: a browser cannot write into ~/.claude.</summary>
    [Authorize]
    [HttpPost("api/sessions/{id}/transfer/take-local")]
    public Task<IActionResult> TakeLocal(string id, [FromBody] TakeLocalRequest body, CancellationToken ct)
        => Map(async () => Ok(await _transfer.TakeLocalAsync(LoginOwner, id, body.Directory, body.Confirm, withLink: true, ct)));

    [Authorize]
    [HttpPost("api/sessions/{id}/transfer/hand-back-link")]
    public Task<IActionResult> HandBackLink(string id, [FromBody] HandBackLinkRequest body, CancellationToken ct)
        => Map(async () => Ok(await _transfer.HandBackLinkAsync(LoginOwner, id, body.Directory, body.Resume, ct)));

    // ------------------------------------------------------------------ single-use link

    /// <summary>
    /// Every failure to redeem answers the same 404: the secret is the only credential here, and
    /// telling an expired one from an unknown one would help someone guessing.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("api/session-transfer/{secret}")]
    public async Task<IActionResult> LinkDownload(string secret, CancellationToken ct)
    {
        if (await _transfer.RedeemAsync(secret, SessionTransferDirection.TakeLocal, ct) is not { } ticket)
            return NotFound(new { code = "link_invalid", error = "The link is unknown, expired or already used. Create a new one." });
        return await Map(async () => Archive(
            await _transfer.LocalArchiveAsync(ticket.Owner, ticket.SessionId, ticket.Directory, ct), ticket.SessionId));
    }

    [AllowAnonymous]
    [HttpPut("api/session-transfer/{secret}")]
    [RequestSizeLimit(SessionStateTransfer.MaxArchiveBytes)]
    public async Task<IActionResult> LinkUpload(string secret, CancellationToken ct)
    {
        if (await _transfer.RedeemAsync(secret, SessionTransferDirection.HandBack, ct) is not { } ticket)
            return NotFound(new { code = "link_invalid", error = "The link is unknown, expired or already used. Create a new one." });
        return await Map(async () => Ok(await _transfer.HandBackAsync(ticket.Owner, ticket.SessionId, Request.Body, ticket.Resume, ct)));
    }

    // ------------------------------------------------------------------ plumbing

    private string LoginOwner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    private async Task<string?> TokenOwnerAsync(CancellationToken ct)
        => RemoteBearerToken.Read(Request) is { } token ? (await _findCaller(token, ct))?.Owner : null;

    private async Task<IActionResult> AsToken<T>(Func<string, Task<T>> action, CancellationToken ct)
    {
        var owner = await TokenOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        return await Map(async () => Ok(await action(owner)));
    }

    private FileContentResult Archive(LocalTranscriptArchive archive, string sessionId)
    {
        Response.Headers["X-AgentHub-Resume-Id"] = archive.ResumeId;
        Response.Headers["X-AgentHub-Project-Folder"] = archive.ProjectFolder;
        return File(archive.Archive, "application/gzip", $"{sessionId}-conversation.tgz");
    }

    private async Task<IActionResult> Map(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (KeyNotFoundException) { return NotFound(new { code = "session_not_found", error = "Session not found." }); }
        catch (SessionTransferException e)
        {
            var status = e.Code switch
            {
                "directory_invalid" or "upload_invalid" or "agent_not_supported" => StatusCodes.Status400BadRequest,
                "transcript_missing" => StatusCodes.Status404NotFound,
                "storage_unavailable" or "origin_not_configured" => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status409Conflict
            };
            return StatusCode(status, new { code = e.Code, error = e.Message });
        }
        catch (ArgumentException e) { return BadRequest(new { code = "transfer_refused", error = e.Message }); }
        catch (InvalidOperationException e) { return Conflict(new { code = "transfer_refused", error = e.Message }); }
    }
}
