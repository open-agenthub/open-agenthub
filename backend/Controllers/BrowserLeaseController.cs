using AgentHub.Api.Browser;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("internal/browser-leases/{leaseId}/state")]
public sealed class BrowserLeaseController(IBrowserService browsers) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> State(string leaseId, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Browser-Token", out var token) ||
            token.Count != 1 || token[0] is null || token[0]!.Length > 512)
            return Unauthorized();
        var urls = await browsers.MintStateUrlsAsync(leaseId, token[0]!, ct);
        return urls is null ? Unauthorized() : Ok(urls);
    }
}