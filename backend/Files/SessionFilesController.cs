using System.Net.Http.Headers;
using System.Security.Claims;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Files;

public sealed record ReserveSessionFileRequest(
    string Name,
    string MimeType,
    long Size,
    string? BatchId);

public sealed record SetFilePresentationRequest(string? FileId);
public sealed record FileApiError(string Code, string Message);

public sealed record SessionFileResponse(
    string Id,
    string Name,
    string MimeType,
    long Size,
    string State,
    string PreviewState,
    string Source,
    DateTime? ExpiresAt,
    string? PreviewFileId);

[ApiController]
[Authorize]
[Route("api/sessions/{id}/files")]
public sealed class SessionFilesController(
    ISessionAccessService access,
    ISessionFileService files,
    IArtifactStore artifacts,
    SessionFileOptions options) : ControllerBase
{
    [HttpGet("capabilities")]
    public async Task<IActionResult> Capabilities(string id, CancellationToken ct)
    {
        var resolved = await ResolveAsync(id, ct);
        return resolved is null
            ? NotFound()
            : Ok(SessionFileApi.Capabilities(
                artifacts.IsConfigured,
                SessionAccessRules.CanWriteFiles(resolved.Level),
                options));
    }

    [HttpPost("reserve")]
    public async Task<IActionResult> Reserve(
        string id,
        [FromBody] ReserveSessionFileRequest request,
        CancellationToken ct)
    {
        var actor = await ResolveActorAsync(id, ct);
        if (actor is null) return NotFound();
        if (!actor.CanWrite) return Forbid();

        try
        {
            var reserved = await files.ReserveAsync(actor,
                new ReserveSessionFileCommand(
                    request.Name, request.MimeType, request.Size, request.BatchId, "user"), ct);
            return Ok(new
            {
                file = SessionFileApi.Response(reserved.File),
                upload = reserved.Upload,
            });
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    [HttpPut("{fileId}/content")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Upload(
        string id,
        string fileId,
        CancellationToken ct)
    {
        if (SessionFileApi.LimitUploadBody(this, options) is { } tooLarge) return tooLarge;
        var actor = await ResolveActorAsync(id, ct);
        if (actor is null) return NotFound();
        if (!actor.CanWrite) return Forbid();

        try
        {
            await files.PutPodContentAsync(actor, fileId, Request.Body, Request.ContentLength, ct);
            return NoContent();
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    [HttpPost("{fileId}/complete")]
    public async Task<IActionResult> Complete(
        string id,
        string fileId,
        CancellationToken ct)
    {
        var actor = await ResolveActorAsync(id, ct);
        if (actor is null) return NotFound();
        if (!actor.CanWrite) return Forbid();

        try
        {
            return Ok(SessionFileApi.Response(await files.CompleteAsync(actor, fileId, ct)));
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    [HttpGet]
    public async Task<IActionResult> List(string id, CancellationToken ct)
    {
        var resolved = await ResolveAsync(id, ct);
        if (resolved is null) return NotFound();
        if (!SessionAccessRules.CanReadFiles(resolved.Level)) return Forbid();
        var actor = SessionFileApi.Actor(resolved, Principal);

        var listed = await files.ListAsync(actor, ct);
        return Ok(listed.Where(file => file.State == SessionFileState.Ready)
            .Select(SessionFileApi.Response).ToArray());
    }

    [HttpGet("{fileId}/content")]
    public async Task<IActionResult> Content(
        string id,
        string fileId,
        CancellationToken ct)
    {
        var actor = await ResolveActorAsync(id, ct);
        if (actor is null) return NotFound();

        try
        {
            return SessionFileApi.Content(this, await files.OpenContentRangeAsync(
                actor, fileId, SessionFileApi.RequestedRange(Request), ct: ct));
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    /// <summary>
    /// Issues a url the Files pane can hand to a &lt;video&gt; element, which fetches its source
    /// itself and cannot send the bearer header (see <see cref="SessionFileStreamTickets"/>).
    /// </summary>
    [HttpPost("{fileId}/stream")]
    public async Task<IActionResult> IssueStream(
        string id,
        string fileId,
        [FromServices] SessionFileStreamTickets tickets,
        CancellationToken ct)
    {
        SessionFileApi.KeepTicketPrivate(Response);
        var resolved = await ResolveAsync(id, ct);
        if (resolved is null) return NotFound();
        if (!SessionAccessRules.CanReadFiles(resolved.Level)) return Forbid();
        try
        {
            return Ok(await tickets.IssueAsync(SessionFileApi.Actor(resolved, Principal), fileId, ct));
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    /// <summary>
    /// Serves a video to whoever holds a ticket for it, honouring Range so the player can seek.
    /// The ticket only says who asked; access is resolved for that principal on every request,
    /// exactly as for the bearer route, so a revoked share stops the next range. That case is a
    /// 403 rather than the 404 of an unknown ticket: the ticket was genuine, and the player can
    /// tell "you lost access" from "this link is wrong or expired" (which a fresh ticket fixes).
    /// </summary>
    [HttpGet("{fileId}/stream")]
    [AllowAnonymous]
    public async Task<IActionResult> Stream(
        string id,
        string fileId,
        [FromQuery] string? ticket,
        [FromServices] SessionFileStreamTickets tickets,
        CancellationToken ct)
    {
        SessionFileApi.KeepTicketPrivate(Response);
        var principal = await tickets.RedeemAsync(ticket, id, fileId, ct);
        if (principal is null) return NotFound();
        var resolved = await access.ResolveUserAsync(principal, id, ct);
        if (resolved is null || !SessionAccessRules.CanReadFiles(resolved.Level))
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }
        try
        {
            return SessionFileApi.Content(this, await files.OpenContentRangeAsync(
                SessionFileApi.Actor(resolved, principal), fileId,
                SessionFileApi.RequestedRange(Request), ct: ct));
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    [HttpDelete("{fileId}")]
    public async Task<IActionResult> Delete(
        string id,
        string fileId,
        CancellationToken ct)
    {
        var actor = await ResolveActorAsync(id, ct);
        if (actor is null) return NotFound();
        if (!actor.CanManage) return Forbid();
        try
        {
            await files.DeleteAsync(actor, fileId, ct);
            return NoContent();
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    [HttpGet("presentation")]
    public async Task<IActionResult> Presentation(string id, CancellationToken ct)
    {
        var actor = await ResolveActorAsync(id, ct);
        return actor is null
            ? NotFound()
            : Ok(await files.GetPresentationAsync(actor, ct));
    }

    [HttpPut("presentation")]
    public async Task<IActionResult> SetPresentation(
        string id,
        [FromBody] SetFilePresentationRequest request,
        CancellationToken ct)
    {
        var actor = await ResolveActorAsync(id, ct);
        if (actor is null) return NotFound();
        if (!actor.CanWrite) return Forbid();
        try
        {
            return Ok(await files.SetPresentationAsync(actor, request.FileId, ct));
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    private string Principal =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    private Task<SessionAccessResult?> ResolveAsync(string id, CancellationToken ct) =>
        access.ResolveUserAsync(Principal, id, ct);

    private async Task<SessionFileActor?> ResolveActorAsync(string id, CancellationToken ct)
    {
        var resolved = await ResolveAsync(id, ct);
        return resolved is null ? null : SessionFileApi.Actor(resolved, Principal);
    }
}

public static class SessionFileApi
{
    private static readonly string[] DirectPreviewMimeTypes =
    [
        "image/svg+xml", "image/png", "image/jpeg", "image/webp", "image/gif",
        "application/pdf", "text/markdown", "text/plain",
        VideoContainerInspector.Mp4, VideoContainerInspector.WebM,
    ];

    public static SessionFileActor Actor(SessionAccessResult access, string principal) => new(
        access.Session.Id,
        access.Session.Owner,
        principal,
        SessionAccessRules.CanWriteFiles(access.Level),
        SessionAccessRules.CanManageFiles(access.Level));

    public static SessionFileResponse Response(SessionFileRecord file) => new(
        file.Id,
        file.Name,
        file.DetectedMimeType ?? file.DeclaredMimeType,
        file.Size,
        file.State.ToString(),
        file.PreviewState.ToString(),
        file.Source,
        file.ExpiresAt,
        file.PreviewFileId);

    public static SessionFileCapabilities Capabilities(
        bool s3Configured,
        bool canWrite,
        SessionFileOptions options) => new(
            s3Configured ? "s3" : "temporary-pod",
            canWrite,
            DirectPreviewMimeTypes,
            new Dictionary<string, string>
            {
                ["text/html"] = "download_only",
                ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] =
                    options.OfficePreview.Enabled ? "office_preview_pending" : "office_preview_disabled",
                ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] =
                    options.OfficePreview.Enabled ? "office_preview_pending" : "office_preview_disabled",
                ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] =
                    options.OfficePreview.Enabled ? "office_preview_pending" : "office_preview_disabled",
            },
            options.OfficePreview.Enabled,
            options.OfficePreview.Enabled ? "available" : "disabled",
            options);

    public static IActionResult Content(ControllerBase controller, FileContentResult content)
    {
        ApplyContentHeaders(controller.Response, content.MimeType, content.Name);
        if (content.RedirectUrl is not null)
        {
            return controller.Redirect(content.RedirectUrl);
        }

        controller.Response.Headers.AcceptRanges = "bytes";
        if (content.RangeNotSatisfiable)
        {
            controller.Response.Headers.ContentRange = $"bytes */{content.Size}";
            return controller.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        return content.Range is null
            ? controller.File(content.Content!, content.MimeType)
            : new SessionFileContentResult(content);
    }

    /// <summary>
    /// Headers for every answer that carries or is fetched with a stream ticket, errors included.
    /// The ticket is a credential in a url: no-referrer keeps a media document opened on it from
    /// sending that url on as a Referer, and no-store keeps the response — and with it the url as
    /// a cache key — out of shared and on-disk caches.
    /// </summary>
    public static void KeepTicketPrivate(HttpResponse response)
    {
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.CacheControl = "private, no-store";
    }

    /// <summary>The single byte range a GET asks for, or null to serve the whole file.</summary>
    public static ByteRangeRequest? RequestedRange(HttpRequest request) =>
        ByteRangeRequest.Parse(request.Headers.Range.ToString());

    /// <summary>
    /// Raises the request body limit of an upload route to the largest file any type may
    /// reserve. A fixed attribute cannot follow MaxVideoBytes from the chart, and the global
    /// Kestrel limit sits at the document size. The reserved size, enforced while streaming,
    /// stays the real bound for each file; this only stops a body no reservation could match.
    /// </summary>
    public static IActionResult? LimitUploadBody(ControllerBase controller, SessionFileOptions options)
    {
        var maximum = SessionFileValidator.MaxUploadBytes(options);
        if (controller.Request.ContentLength > maximum)
        {
            return controller.StatusCode(StatusCodes.Status413PayloadTooLarge,
                new FileApiError("file_too_large", "file_too_large"));
        }

        var feature = controller.HttpContext.Features
            .Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maximum;
        }

        return null;
    }

    public static IActionResult Error(ControllerBase controller, SessionFileException exception)
    {
        var response = new FileApiError(exception.Code, exception.Message);
        return exception.Code switch
        {
            "file_not_found" => controller.NotFound(response),
            "stream_unsupported" or "file_not_ready" => controller.BadRequest(response),
            "file_access_denied" => controller.StatusCode(StatusCodes.Status403Forbidden, response),
            "file_content_expired" => controller.StatusCode(StatusCodes.Status410Gone, response),
            "file_too_large" or "attachment_bytes_exceeded" =>
                controller.StatusCode(StatusCodes.Status413PayloadTooLarge, response),
            "temporary_storage_unavailable" or "storage_verification_failed"
                or "file_state_conflict" or "session_file_count_exceeded"
                or "session_file_bytes_exceeded" => controller.Conflict(response),
            _ => controller.BadRequest(response),
        };
    }

    private static void ApplyContentHeaders(
        HttpResponse response,
        string mimeType,
        string name)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        var disposition = new ContentDispositionHeaderValue(
            mimeType != "image/svg+xml" &&
            DirectPreviewMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase)
                ? "inline"
                : "attachment")
        {
            FileNameStar = name,
            FileName = name,
        };
        response.Headers.ContentDisposition = disposition.ToString();
        if (mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        }
    }
}

/// <summary>
/// Writes the requested range of a file as a 206.
///
/// FileStreamResult can only serve ranges of a seekable stream, and none of ours is: the bytes
/// come from object storage or the session pod as a response body. The range is therefore
/// asked of the source and this only frames the answer.
/// </summary>
public sealed class SessionFileContentResult(FileContentResult content) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var response = context.HttpContext.Response;
        var range = content.Range!;
        await using var body = content.Content!;
        response.ContentType = content.MimeType;
        response.StatusCode = StatusCodes.Status206PartialContent;
        response.Headers.ContentRange = $"bytes {range.From}-{range.To}/{content.Size}";
        response.ContentLength = range.Length;
        await body.CopyToAsync(response.Body, context.HttpContext.RequestAborted);
    }
}
