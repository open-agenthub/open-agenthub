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
    [RequestSizeLimit(52L * 1024 * 1024)]
    public async Task<IActionResult> Upload(
        string id,
        string fileId,
        CancellationToken ct)
    {
        var actor = await ResolveActorAsync(id, ct);
        if (actor is null) return NotFound();
        if (!actor.CanWrite) return Forbid();
        if (Request.ContentLength > options.MaxDocumentBytes)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new FileApiError("file_too_large", "file_too_large"));
        }

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
            return SessionFileApi.Content(this, await files.OpenContentAsync(actor, fileId, ct));
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
        "image/png", "image/jpeg", "image/webp", "image/gif",
        "application/pdf", "text/markdown", "text/plain",
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
                ["image/svg+xml"] = "download_only",
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

        return controller.File(content.Content!, content.MimeType);
    }

    public static IActionResult Error(ControllerBase controller, SessionFileException exception)
    {
        var response = new FileApiError(exception.Code, exception.Message);
        return exception.Code switch
        {
            "file_not_found" => controller.NotFound(response),
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
            DirectPreviewMimeTypes.Contains(mimeType, StringComparer.OrdinalIgnoreCase)
                ? "inline"
                : "attachment")
        {
            FileNameStar = name,
            FileName = name,
        };
        response.Headers.ContentDisposition = disposition.ToString();
        if (mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
        }
    }
}
