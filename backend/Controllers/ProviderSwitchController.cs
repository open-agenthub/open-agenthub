using System.Security.Claims;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/sessions/{id}/provider-switch")]
public sealed class ProviderSwitchController(ISessionService sessions) : ControllerBase
{
    private string Owner => User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    private async Task<IActionResult> Execute(Func<KubernetesSessionService, Task<object?>> action)
    {
        if (sessions is not KubernetesSessionService service) return StatusCode(503, "Provider replacement is unavailable.");
        Response.Headers.CacheControl = "no-store";
        try { return Ok(await action(service)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException error) { return BadRequest(error.Message); }
        catch (AgentHub.Api.Agents.AgentNotAllowedException) { return StatusCode(403, "The target provider is not enabled."); }
        catch (InvalidOperationException error) { return Conflict(error.Message); }
    }

    [HttpGet]
    public Task<IActionResult> Status(string id, CancellationToken ct) =>
        Execute(async service => await service.ProviderSwitchStatusAsync(Owner, id, ct));

    [HttpPost("preview")]
    [RequestSizeLimit(80_000)]
    public Task<IActionResult> Preview(string id, ProviderSwitchSelection selection, CancellationToken ct) =>
        Execute(async service => await service.ReviewProviderSwitchAsync(Owner, id, selection, ct));

    [HttpPost]
    [RequestSizeLimit(80_000)]
    public Task<IActionResult> Start(string id, ProviderSwitchSelection selection, CancellationToken ct) =>
        Execute(async service => await service.StartProviderSwitchAsync(Owner, id, selection, ct));

    public sealed record OperationRequest(Guid RequestId);
    [HttpPost("retry")]
    public Task<IActionResult> Retry(string id, OperationRequest request, CancellationToken ct) => Execute(async service =>
    {
        await service.RetryProviderSwitchAsync(Owner, id, request.RequestId, ct);
        return await service.ProviderSwitchStatusAsync(Owner, id, ct);
    });

    [HttpPost("cancel")]
    public Task<IActionResult> Cancel(string id, OperationRequest request, CancellationToken ct) => Execute(async service =>
    {
        await service.CancelProviderSwitchAsync(Owner, id, request.RequestId, ct);
        return await service.ProviderSwitchStatusAsync(Owner, id, ct);
    });
}
