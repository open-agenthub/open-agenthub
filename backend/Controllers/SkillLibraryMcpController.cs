using System.Text.Json;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Streamable-HTTP endpoint of the skill-library MCP server. Agent pods reach
/// it with their session callback token; the entry is injected into every
/// session's .mcp.json (see KubernetesSessionService). Requests are plain
/// JSON-RPC POSTs answered with JSON — no SSE stream is offered.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("internal/sessions/{id}/mcp")]
public sealed class SkillLibraryMcpController : ControllerBase
{
    private const int MaxBodyBytes = 1_000_000;

    private readonly ISessionStore _store;
    private readonly SkillLibraryMcpService _mcp;
    private readonly SkillImporter _importer;

    public SkillLibraryMcpController(ISessionStore store, SkillLibraryMcpService mcp, SkillImporter importer)
    {
        _store = store;
        _mcp = mcp;
        _importer = importer;
    }

    [HttpPost]
    public async Task<IActionResult> Post(string id, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Agent-Token", out var token))
            return Unauthorized();
        var session = await _store.GetByCallbackTokenAsync(token!, ct);
        if (session is null || session.Id != id)
            return Unauthorized();

        if (Request.ContentLength is > MaxBodyBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge);

        JsonDocument message;
        try
        {
            message = await JsonDocument.ParseAsync(Request.Body, cancellationToken: ct);
        }
        catch (JsonException)
        {
            return BadRequest(new { error = "Invalid JSON." });
        }

        using (message)
        {
            var response = await _mcp.HandleAsync(message.RootElement, session, ct);
            if (response is null)
                return Accepted();
            return Content(response.ToJsonString(), "application/json");
        }
    }

    /// <summary>No server-initiated stream: clients must use plain POSTs.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        Response.Headers.Allow = "POST";
        return StatusCode(StatusCodes.Status405MethodNotAllowed);
    }

    /// <summary>
    /// Upward sync: the agent pod reports skills it created locally under
    /// ~/.claude/skills (unmanaged directories); unchanged ones are skipped.
    /// Called by the entrypoint after start and on graceful shutdown.
    /// </summary>
    [HttpPost("/internal/sessions/{id}/skills/import")]
    public async Task<IActionResult> Import(
        string id, [FromBody] ImportSkillsRequest request, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Agent-Token", out var token))
            return Unauthorized();
        var session = await _store.GetByCallbackTokenAsync(token!, ct);
        if (session is null || session.Id != id)
            return Unauthorized();
        if (request.Skills is null || request.Skills.Count == 0)
            return Ok(new ImportSkillsResult([], [], [], []));
        if (request.Skills.Count > 50)
            return BadRequest(new { error = "Too many skills in one import." });

        return Ok(await _importer.ImportAsync(session, request.Skills, ct));
    }
}
