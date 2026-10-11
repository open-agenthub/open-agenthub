using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Persistence;
using AgentHub.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Files;

[ApiController]
[AllowAnonymous]
[Route("api/remote/sessions/{id}/files")]
public sealed class RemoteSessionFilesController : ControllerBase
{
    private readonly Func<string, CancellationToken, Task<RemoteCaller?>> findCaller;
    private readonly ISessionAccessService access;
    private readonly ISessionFileService files;

    [ActivatorUtilitiesConstructor]
    public RemoteSessionFilesController(ApiTokenStore tokens, ISessionAccessService access, ISessionFileService files)
        : this(tokens.FindCallerByTokenAsync, access, files) { }

    public RemoteSessionFilesController(Func<string, CancellationToken, Task<RemoteCaller?>> findCaller,
        ISessionAccessService access, ISessionFileService files)
    {
        this.findCaller = findCaller;
        this.access = access;
        this.files = files;
    }

    [HttpGet]
    public async Task<IActionResult> List(string id, CancellationToken ct)
    {
        var (actor, error) = await ResolveAsync(id, ct);
        if (error is not null) return error;
        return Ok((await files.ListAsync(actor!, ct))
            .Where(file => file.State == SessionFileState.Ready).Select(SessionFileApi.Response).ToArray());
    }

    [HttpGet("{fileId}/content")]
    public async Task<IActionResult> Content(string id, string fileId, CancellationToken ct)
    {
        var (actor, error) = await ResolveAsync(id, ct);
        if (error is not null) return error;
        try
        {
            // A workstation may reach the hub but not its object store. Proxy the content and
            // never ask a client to follow a storage redirect with its personal bearer token.
            return SessionFileApi.Content(this,
                await files.OpenContentAsync(actor!, fileId, allowRedirect: false, ct: ct));
        }
        catch (SessionFileException exception) { return SessionFileApi.Error(this, exception); }
    }

    private async Task<(SessionFileActor? Actor, IActionResult? Error)> ResolveAsync(string id, CancellationToken ct)
    {
        var caller = RemoteBearerToken.Read(Request) is { } token ? await findCaller(token, ct) : null;
        if (caller is null) return (null, Unauthorized());
        var resolved = await access.ResolveUserAsync(caller.Owner, id, ct);
        if (resolved is null) return (null, NotFound());
        if (!SessionAccessRules.CanReadFiles(resolved.Level)) return (null, StatusCode(403));
        return (SessionFileApi.Actor(resolved, caller.Owner), null);
    }
}
