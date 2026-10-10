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
public sealed record ProjectSessionFile(
    string SessionId,
    string SessionTitle,
    string Id,
    string Name,
    string MimeType,
    long Size,
    DateTime? CompletedAt);
public sealed record ProjectSessionFilesResponse(
    IReadOnlyList<ProjectSessionFile> Files,
    bool Truncated);

[ApiController]
[AllowAnonymous]
[Route("internal/sessions/{id}/files")]
public sealed class InternalSessionFilesController(
    IAgentCallbackAuthorizer authorizer,
    ISessionFileService files,
    IArtifactStore artifacts,
    SessionFileOptions options,
    IProjectFileAccess projectFiles) : ControllerBase
{
    // The in-pod client refuses a response over a megabyte, and a project of long-lived sessions
    // can hold more file rows than fit in one. Past this the listing says it was cut short and the
    // caller narrows it to one session, rather than failing as a whole on size.
    public const int MaxProjectFiles = 500;

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

    /// <summary>
    /// Streams a file's content to the agent through the API.
    ///
    /// The agent used to read S3-backed files straight from the presigned url that materialize
    /// hands out. That url lives for PresignMinutes, so anything that did not fetch it within
    /// that window — an agent that listed files first and read one later, or one that kept the
    /// url in its context — got a signature error and reported the file as missing. This route
    /// has no deadline, keeps the storage credential out of the agent's reach entirely, and works
    /// even where the pod cannot reach object storage directly.
    /// </summary>
    [HttpGet("{fileId}/content")]
    public async Task<IActionResult> Content(string id, string fileId, CancellationToken ct)
    {
        var actor = await ActorAsync(id, ct);
        if (actor is null) return Unauthorized();
        try
        {
            return SessionFileApi.Content(
                this, await files.OpenContentAsync(actor, fileId, allowRedirect: false, ct));
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

    /// <summary>
    /// Lists the ready files of the caller's sibling sessions — same owner, same project
    /// (docs/project-files.md). Read-only by construction: nothing under this prefix reserves,
    /// uploads, deletes or presents, so a sibling's files cannot be changed from here.
    ///
    /// The caller is identified by its own callback token and nothing else. The siblings are
    /// resolved here, from that session's stored owner and project; the request names at most a
    /// session to narrow the listing to, and an id it may not read narrows it to nothing.
    /// </summary>
    [HttpGet("project")]
    public async Task<IActionResult> ProjectFiles(
        string id, [FromQuery] string? sessionId, CancellationToken ct)
    {
        var caller = await AuthorizeAsync(id, ct);
        if (caller is null) return Unauthorized();
        IReadOnlyList<SessionRecord> siblings;
        if (string.IsNullOrEmpty(sessionId))
        {
            siblings = await projectFiles.SiblingsAsync(caller, ct);
        }
        else
        {
            var sibling = await projectFiles.ResolveSiblingAsync(caller, sessionId, ct);
            if (sibling is null) return NotFound();
            siblings = [sibling];
        }

        var listed = new List<ProjectSessionFile>();
        var truncated = false;
        foreach (var sibling in siblings.OrderBy(session => session.CreatedAt)
                     .ThenBy(session => session.Id, StringComparer.Ordinal))
        {
            var ready = (await files.ListAsync(ReaderOf(sibling), ct))
                .Where(file => file.State == SessionFileState.Ready);
            foreach (var file in ready)
            {
                if (listed.Count == MaxProjectFiles)
                {
                    truncated = true;
                    break;
                }
                listed.Add(new ProjectSessionFile(
                    sibling.Id,
                    sibling.Title,
                    file.Id,
                    file.Name,
                    file.DetectedMimeType ?? file.DeclaredMimeType,
                    file.Size,
                    file.CompletedAt));
            }
            if (truncated) break;
        }
        return Ok(new ProjectSessionFilesResponse(listed, truncated));
    }

    /// <summary>
    /// Streams one file of a sibling session. Every refusal is the same bare 404 — unknown
    /// session, another owner's, another project's, a caller without a project, an unknown file —
    /// because any difference between them, a status or a body, would let a session probe which
    /// ids exist outside its project.
    /// </summary>
    [HttpGet("project/{sessionId}/{fileId}/content")]
    public async Task<IActionResult> ProjectFileContent(
        string id, string sessionId, string fileId, CancellationToken ct)
    {
        var caller = await AuthorizeAsync(id, ct);
        if (caller is null) return Unauthorized();
        var sibling = await projectFiles.ResolveSiblingAsync(caller, sessionId, ct);
        if (sibling is null) return NotFound();
        try
        {
            return SessionFileApi.Content(
                this,
                await files.OpenContentAsync(ReaderOf(sibling), fileId, allowRedirect: false, ct));
        }
        catch (SessionFileException exception) when (exception.Code == "file_not_found")
        {
            return NotFound();
        }
        catch (SessionFileException exception)
        {
            return SessionFileApi.Error(this, exception);
        }
    }

    // The actor a sibling's files are read as. Neither flag is set, so even if a write path were
    // ever reached with it, the service's own checks refuse.
    private static SessionFileActor ReaderOf(SessionRecord sibling) =>
        new(sibling.Id, sibling.Owner, "agent", false, false);

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
