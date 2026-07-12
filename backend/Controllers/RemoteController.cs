using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Remote session API authenticated by a personal API token (see
/// <see cref="ApiTokensController"/>) rather than an interactive login.
/// Auth is handled manually here via the Authorization: Bearer header, so the
/// global authentication pipeline is left untouched.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/remote")]
public sealed class RemoteController : ControllerBase
{
    private readonly ApiTokenStore _tokens;
    private readonly ISessionService _svc;

    public RemoteController(ApiTokenStore tokens, ISessionService svc)
    {
        _tokens = tokens; _svc = svc;
    }

    /// <summary>Resolves the bearer token to its owner, or null if missing/invalid.</summary>
    private async Task<string?> ResolveOwnerAsync(CancellationToken ct)
    {
        var header = Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;

        var token = header[scheme.Length..].Trim();
        if (string.IsNullOrEmpty(token) || !token.StartsWith("oah_", StringComparison.Ordinal)) return null;

        return await _tokens.FindOwnerByTokenAsync(token, ct);
    }

    [HttpPost("sessions")]
    public async Task<ActionResult<SessionInfo>> Create([FromBody] CreateSessionRequest req, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        try { return Ok(await _svc.CreateSessionAsync(owner, req, ct)); }
        catch (ArgumentException e) { return BadRequest(e.Message); }
    }

    [HttpGet("sessions/{id}")]
    public async Task<ActionResult<SessionInfo>> Get(string id, CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        return await _svc.GetSessionAsync(owner, id, ct) is { } s ? Ok(s) : NotFound();
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<IReadOnlyList<SessionInfo>>> List(CancellationToken ct)
    {
        var owner = await ResolveOwnerAsync(ct);
        if (owner is null) return Unauthorized();
        return Ok(await _svc.ListSessionsAsync(owner, ct));
    }
}
