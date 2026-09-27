using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Files;

public sealed record MaterializeSessionFilesRequest(IReadOnlyList<string> FileIds);
public sealed record InternalReserveFileResponse(
    SessionFileResponse File,
    FileUploadDescriptor Upload);
public sealed record MaterializedSessionFile(
    string Id,
    string SessionId,
    string Name,
    string MimeType,
    long Size,
    string State,
    string StorageKind,
    string? Locator,
    string? DownloadUrl);

[ApiController]
[AllowAnonymous]
[Route("internal/sessions/{id}/files")]
public sealed class InternalSessionFilesController(
    IAgentCallbackAuthorizer authorizer,
    ISessionFileService files,
    IArtifactStore artifacts,
    SessionFileOptions options) : ControllerBase
{
    [HttpGet("capabilities")]
    public async Task<IActionResult> Capabilities(string id, CancellationToken ct) =>
        await AuthorizeAsync(id, ct) is null
            ? Unauthorized()
            : Ok(SessionFileApi.Capabilities(artifacts.IsConfigured, true, options));

    [HttpGet]
    public async Task<IActionResult> List(string id, CancellationToken ct)
    {
        var actor = await ActorAsync(id, ct);
        if (actor is null) return Unauthorized();
        var listed = await files.ListAsync(actor, ct);
        return Ok(listed.Where(file => file.State == SessionFileState.Ready)
            .Select(SessionFileApi.Response).ToArray());
    }

    [HttpPost("materialize")]
    public async Task<IActionResult> Materialize(
        string id,
        [FromBody] MaterializeSessionFilesRequest request,
        CancellationToken ct)
    {
        var actor = await ActorAsync(id, ct);
        if (actor is null) return Unauthorized();
        if (request.FileIds is null || request.FileIds.Count is < 1 or > 5 ||
            request.FileIds.Distinct(StringComparer.Ordinal).Count() != request.FileIds.Count)
        {
            return BadRequest(new FileApiError("invalid_file_ids", "invalid_file_ids"));
        }

        var ready = (await files.ListAsync(actor, ct))
            .Where(file => file.State == SessionFileState.Ready)
            .ToDictionary(file => file.Id, StringComparer.Ordinal);
        var result = new List<MaterializedSessionFile>(request.FileIds.Count);
        foreach (var fileId in request.FileIds)
        {
            if (!ready.TryGetValue(fileId, out var file)) return NotFound();
            result.Add(new MaterializedSessionFile(
                file.Id,
                file.SessionId,
                file.Name,
                file.DetectedMimeType ?? file.DeclaredMimeType,
                file.Size,
                file.State.ToString(),
                file.StorageKind.ToString(),
                file.StorageKind == SessionFileStorageKind.Pod ? file.StorageLocator : null,
                file.StorageKind == SessionFileStorageKind.S3
                    ? artifacts.PresignGet(file.StorageLocator,
                        TimeSpan.FromMinutes(options.PresignMinutes))
                    : null));
        }
        return Ok(result);
    }

    [HttpPost("reserve")]
    public async Task<IActionResult> Reserve(
        string id,
        [FromBody] ReserveSessionFileRequest request,
        CancellationToken ct)
    {
        var actor = await ActorAsync(id, ct);
        if (actor is null) return Unauthorized();
        try
        {
            var reserved = await files.ReserveAsync(actor,
                new ReserveSessionFileCommand(
                    request.Name, request.MimeType, request.Size, request.BatchId, "agent"), ct);
            var upload = reserved.Upload.Kind == "proxy"
                ? reserved.Upload with
                {
                    Url = $"/internal/sessions/{Uri.EscapeDataString(id)}/files/" +
                        $"{Uri.EscapeDataString(reserved.File.Id)}/content",
                }
                : reserved.Upload;
            return Ok(new InternalReserveFileResponse(
                SessionFileApi.Response(reserved.File), upload));
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    [HttpPut("{fileId}/content")]
    [RequestSizeLimit(52L * 1024 * 1024)]
    public async Task<IActionResult> Upload(
        string id, string fileId, CancellationToken ct)
    {
        var actor = await ActorAsync(id, ct);
        if (actor is null) return Unauthorized();
        if (Request.ContentLength > options.MaxDocumentBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new FileApiError("file_too_large", "file_too_large"));
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
        string id, string fileId, CancellationToken ct)
    {
        var actor = await ActorAsync(id, ct);
        if (actor is null) return Unauthorized();
        try
        {
            return Ok(SessionFileApi.Response(
                await files.CompleteAsync(actor, fileId, ct)));
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    [HttpPut("presentation")]
    public async Task<IActionResult> Presentation(
        string id,
        [FromBody] SetFilePresentationRequest request,
        CancellationToken ct)
    {
        var actor = await ActorAsync(id, ct);
        if (actor is null) return Unauthorized();
        try
        {
            return Ok(await files.SetPresentationAsync(actor, request.FileId, ct));
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    private Task<SessionRecord?> AuthorizeAsync(string id, CancellationToken ct) =>
        authorizer.AuthorizeAsync(Request, id, ct);

    private async Task<SessionFileActor?> ActorAsync(string id, CancellationToken ct)
    {
        var session = await AuthorizeAsync(id, ct);
        return session is null
            ? null
            : new SessionFileActor(session.Id, session.Owner, "agent", true, true);
    }
}
