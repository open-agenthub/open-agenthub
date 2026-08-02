// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — shared session file reads.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Files;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Ee.Sharing;

[ApiController]
[AllowAnonymous]
[Route("api/shared/{token}/files")]
public sealed class SharedSessionFilesController(
    ISessionAccessService access,
    ISessionFileService files,
    IArtifactStore artifacts,
    SessionFileOptions options) : ControllerBase
{
    [HttpGet("capabilities")]
    public async Task<IActionResult> Capabilities(string token, CancellationToken ct)
    {
        var resolved = await access.ResolveTokenReadOnlyAsync(token, ct);
        return resolved is null
            ? NotFound()
            : Ok(SessionFileApi.Capabilities(artifacts.IsConfigured, false, options));
    }

    [HttpGet]
    public async Task<IActionResult> List(string token, CancellationToken ct)
    {
        var actor = await ResolveActorAsync(token, ct);
        if (actor is null) return NotFound();
        var listed = await files.ListAsync(actor, ct);
        return Ok(listed.Where(file => file.State == SessionFileState.Ready)
            .Select(SessionFileApi.Response).ToArray());
    }

    [HttpGet("{fileId}/content")]
    public async Task<IActionResult> Content(
        string token,
        string fileId,
        CancellationToken ct)
    {
        var actor = await ResolveActorAsync(token, ct);
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

    [HttpGet("presentation")]
    public async Task<IActionResult> Presentation(string token, CancellationToken ct)
    {
        var actor = await ResolveActorAsync(token, ct);
        return actor is null
            ? NotFound()
            : Ok(await files.GetPresentationAsync(actor, ct));
    }

    private async Task<SessionFileActor?> ResolveActorAsync(
        string token,
        CancellationToken ct)
    {
        var resolved = await access.ResolveTokenReadOnlyAsync(token, ct);
        return resolved is null ? null : SessionFileApi.Actor(resolved, "share-link");
    }
}
