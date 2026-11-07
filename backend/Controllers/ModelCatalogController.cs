using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// The caller's own stored catalog for an agent and account, for the new-session dialog (#461).
/// No pod is started for it: the list comes from the last probe of a session on that account,
/// and a refresh asks one of the caller's running sessions on it. Only ever the caller's own
/// rows — another user's accounts are not readable through a shared project or session here.
/// </summary>
[ApiController]
[Authorize]
[Route("api/model-catalog")]
public sealed class ModelCatalogController(IModelCatalogStore store, ISessionStore sessions, ISessionModelCatalogs catalogs,
    TimeProvider? time = null) : ControllerBase
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private string? Owner => User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    [HttpGet]
    public async Task<IActionResult> Read([FromQuery] AgentKind agent, [FromQuery] AgentAuthMode authMode,
        [FromQuery] string? credentialId, [FromQuery] string? endpointUrl, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (Owner is not { } owner) return Unauthorized();
        if (!Enum.IsDefined(agent) || !Enum.IsDefined(authMode)) return BadRequest(new { error = "invalid_agent_or_mode" });
        var key = ModelCatalogKey.For(owner, agent, authMode, credentialId, endpointUrl);
        return Ok(ModelCatalogSanitizer.View(await store.GetAsync(key, ct), _time.GetUtcNow()));
    }

    /// <summary>Asks a running session of the caller's on the same account, newest first.</summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromQuery] AgentKind agent, [FromQuery] AgentAuthMode authMode,
        [FromQuery] string? credentialId, [FromQuery] string? endpointUrl, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (Owner is not { } owner) return Unauthorized();
        if (!Enum.IsDefined(agent) || !Enum.IsDefined(authMode)) return BadRequest(new { error = "invalid_agent_or_mode" });
        var key = ModelCatalogKey.For(owner, agent, authMode, credentialId, endpointUrl);
        var candidates = (await sessions.ListAsync(owner, ct))
            .Where(session => session.Mode != SessionMode.Scheduled && ModelCatalogKey.ForSession(session) == key)
            .OrderByDescending(session => session.CreatedAt)
            // Each candidate costs a pod lookup; a person rarely has more than a few on one account.
            .Take(10);
        foreach (var session in candidates)
        {
            if (await catalogs.ProbeAsync(session, refresh: true, ct) is not null)
                return Ok(ModelCatalogSanitizer.View(await store.GetAsync(key, ct), _time.GetUtcNow()));
        }
        return Conflict(new { error = "no_running_session",
            message = "No running session uses this account. Start one, or type the model id." });
    }
}
