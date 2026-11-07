using System.Security.Claims;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// The models of a session's account (#461, docs/design/model-discovery.md). Owners and
/// collaborators may refresh: that starts a probe process in the pod under the owner's login.
/// Viewers read the stored list only — the same list the CLI's own /model menu shows in the
/// terminal they can already watch — without any account identity in it.
/// </summary>
[ApiController]
[Authorize]
[Route("api/sessions/{id}/models")]
public sealed class SessionModelsController(ISessionAccessService access, ISessionModelCatalogs catalogs,
    IModelCatalogStore store, TimeProvider? time = null) : ControllerBase
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    [HttpGet]
    public async Task<IActionResult> Read(string id, [FromQuery] bool refresh, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var principal = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (principal is null) return Unauthorized();
        var resolved = await access.ResolveUserAsync(principal, id, ct);
        if (resolved is null || resolved.Level == SessionAccessLevel.None) return NotFound();
        var session = resolved.Session;
        var canProbe = resolved.Level >= SessionAccessLevel.Collaborator;
        if (refresh && !canProbe) return StatusCode(403, new { error = "refresh_not_allowed" });

        var key = ModelCatalogKey.ForSession(session);
        var stored = await store.GetAsync(key, ct);
        if (canProbe && (refresh || stored is null) && session.Mode != SessionMode.Scheduled)
        {
            await catalogs.ProbeAsync(session, refresh, ct);
            stored = await store.GetAsync(key, ct);
        }
        return Ok(ModelCatalogSanitizer.View(stored, _time.GetUtcNow(), session.ModelName));
    }
}
