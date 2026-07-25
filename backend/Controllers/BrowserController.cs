using AgentHub.Api.Browser;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("internal/sessions/{id}/browser")]
public sealed class BrowserController(
    IBrowserRequestAuthorizer authorizer,
    IBrowserService browsers) : ControllerBase
{
    private async Task<SessionRecord?> AuthorizedAsync(string id, CancellationToken ct)
    {
        Request.Headers.TryGetValue("X-Agent-Token", out var token);
        var source = HttpContext.Connection.RemoteIpAddress;
        return source is null ? null : await authorizer.AuthorizeAsync(
            id, token.FirstOrDefault(), source, ct);
    }

    [HttpPost]
    public async Task<IActionResult> Start(string id, CancellationToken ct)
    {
        var session = await AuthorizedAsync(id, ct);
        return session is null ? Unauthorized() : Ok(await browsers.EnsureAsync(session, HttpContext.Connection.RemoteIpAddress!, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Status(string id, CancellationToken ct) =>
        await AuthorizedAsync(id, ct) is null
            ? Unauthorized()
            : Ok(await browsers.GetSummaryAsync(id, ct));

    [HttpDelete]
    public async Task<IActionResult> Stop(string id, CancellationToken ct)
    {
        if (await AuthorizedAsync(id, ct) is null) return Unauthorized();
        await browsers.StopAsync(id, ct);
        return NoContent();
    }
}