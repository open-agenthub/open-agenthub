using AgentHub.Api.Containers;
using AgentHub.Api.Files;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Helper containers of a session (docs/session-containers.md), called ONLY by the agent pod via
/// the container tools of the in-pod agenthub_sessions MCP server. No user auth; the per-session
/// callback token (header X-Agent-Token) authorizes the caller, like the other /internal routes.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("internal/sessions/{id}/containers")]
public sealed class SessionContainersController(
    IAgentCallbackAuthorizer authorizer,
    SessionContainerService containers) : ControllerBase
{
    public sealed record VolumeBody(string MountPath, string? SizeLimit);

    public sealed record RunBody(
        string Name, string Image, List<string>? Command, List<string>? Args,
        Dictionary<string, string>? Env, List<int>? Ports, string? Cpu, string? Memory,
        List<VolumeBody>? Volumes, bool? WritableRootFilesystem);

    [HttpPost]
    public Task<IActionResult> Run(string id, [FromBody] RunBody body, CancellationToken ct) =>
        Guarded(id, async session => Ok(await containers.RunAsync(session, new ContainerRunRequest
        {
            Name = body.Name ?? "", Image = body.Image ?? "", Command = body.Command, Args = body.Args,
            Env = body.Env, Ports = body.Ports, Cpu = body.Cpu, Memory = body.Memory,
            Volumes = body.Volumes?.Select(v => new ContainerVolumeRequest(v.MountPath, v.SizeLimit)).ToList(),
            WritableRootFilesystem = body.WritableRootFilesystem ?? false
        }, ct)), ct);

    [HttpGet]
    public Task<IActionResult> List(string id, CancellationToken ct) =>
        Guarded(id, async session => Ok(new { containers = await containers.ListAsync(session, ct) }), ct);

    [HttpGet("{name}/logs")]
    public Task<IActionResult> Logs(string id, string name, [FromQuery] int? tail, [FromQuery] bool? previous, CancellationToken ct) =>
        Guarded(id, async session => Ok(new
        {
            name,
            logs = await containers.LogsAsync(session, name, tail ?? 200, previous ?? false, ct) ?? ""
        }), ct);

    [HttpDelete("{name}")]
    public Task<IActionResult> Stop(string id, string name, CancellationToken ct) =>
        Guarded(id, async session => await containers.StopAsync(session, name, ct)
            ? Ok(new { stopped = true, name })
            : NotFound(new { error = $"no container named '{name}'." }), ct);

    private async Task<IActionResult> Guarded(string id, Func<SessionRecord, Task<IActionResult>> action, CancellationToken ct)
    {
        var session = await authorizer.AuthorizeAsync(Request, id, ct);
        if (session is null) return Unauthorized();
        if (!containers.Enabled) return NotFound(new { error = "Session containers are disabled on this instance." });
        // A scheduled run is a job that ends; helpers would outlive it with nobody to stop them.
        // The pod env keeps the tools out of such sessions, this keeps the route closed as well.
        if (session.Mode == SessionMode.Scheduled)
            return BadRequest(new { error = "Scheduled sessions cannot start containers." });
        // Only a live session: the sweep removes the namespace of any other, and a run racing it
        // would recreate what is being deleted.
        if (session.Status is not (SessionStatus.Pending or SessionStatus.Running))
            return Conflict(new { error = "The session is not running." });
        try { return await action(session); }
        catch (ContainerRequestException error) { return BadRequest(new { error = error.Message }); }
        catch (KeyNotFoundException error) { return NotFound(new { error = error.Message }); }
    }
}
