using AgentHub.Api.Notifications;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Ee.Sharing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Endpoints called ONLY by the agent pod. No user auth; instead a per-session
/// callback token (header X-Agent-Token) is used.
/// Not routed externally via the ingress; reachable only inside the cluster.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("internal/sessions/{id}")]
public sealed class InternalController : ControllerBase
{
    private readonly ISessionStore _store;
    private readonly IEnumerable<INotifier> _notifiers;
    private readonly ISessionService _svc;
    private readonly PermissionStore _permissions;
    private readonly IPermissionNotifier _permNotifier;
    private readonly SessionShareStore _shares;

    public InternalController(ISessionStore store, IEnumerable<INotifier> notifiers, ISessionService svc,
        PermissionStore permissions, IPermissionNotifier permNotifier, SessionShareStore shares)
    {
        _store = store; _notifiers = notifiers; _svc = svc;
        _permissions = permissions; _permNotifier = permNotifier; _shares = shares;
    }

    private async Task NotifyAllAsync(SessionRecord rec, string ev, string message, CancellationToken ct)
    {
        foreach (var n in _notifiers)
            await n.NotifyAsync(rec, ev, message, ct);
    }

    public record StatusBody(string Status);
    public record NotifyBody(string Message, string? Event);

    private async Task<SessionRecord?> AuthAsync(string id, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Agent-Token", out var tok)) return null;
        var rec = await _store.GetByCallbackTokenAsync(tok!, ct);
        return rec is not null && rec.Id == id ? rec : null;
    }

    [HttpPost("status")]
    public async Task<IActionResult> Status(string id, [FromBody] StatusBody body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        await _store.UpdateStatusAsync(id, body.Status, ct);
        if (body.Status is "Succeeded" or "Failed")
        {
            await _store.SetQuestionPendingAsync(id, false, ct);
            await NotifyAllAsync(rec, body.Status == "Succeeded" ? "finished" : "failed",
                body.Status == "Succeeded" ? "Task completed." : "Session failed.", ct);
        }
        return NoContent();
    }

    /// <summary>Called by the Claude Code notification hook when the agent is waiting or asks a question.</summary>
    [HttpPost("notify")]
    public async Task<IActionResult> Notify(string id, [FromBody] NotifyBody body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        await _store.SetQuestionPendingAsync(id, true, ct);
        await NotifyAllAsync(rec, body.Event ?? "question",
            string.IsNullOrWhiteSpace(body.Message) ? "The agent is waiting for your reply." : body.Message, ct);
        return NoContent();
    }

    /// <summary>
    /// Persists the session owner's Claude CLI OAuth credentials (subscription login).
    /// The agent pod uploads the file whenever ~/.claude/.credentials.json changes
    /// (first login and token refresh); new sessions get it injected again.
    /// </summary>
    [HttpPut("claude-credentials")]
    public async Task<IActionResult> ClaudeCredentials(string id, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64_000) return BadRequest();

        await _svc.StoreClaudeCredentialsAsync(rec.Owner, json, ct);
        return NoContent();
    }

    /// <summary>
    /// Receives the terminal scrollback and stores it in Postgres so transcripts
    /// are available even without S3 (uploaded periodically and on exit).
    /// </summary>
    [HttpPut("scrollback")]
    public async Task<IActionResult> Scrollback(string id, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        using var reader = new StreamReader(Request.Body);
        var text = await reader.ReadToEndAsync(ct);
        // Cap: matches the agent's in-memory scrollback buffer.
        if (text.Length > 400_000) text = text[^400_000..];
        await _store.SetScrollbackAsync(id, text, ct);
        return NoContent();
    }

    public record PermissionBody(string Tool, string? Input);

    /// <summary>
    /// The agent's PreToolUse hook asks whether a tool may run. If the owner has a Slack
    /// target, we post an interactive prompt and return an id to poll; otherwise we tell
    /// the hook to fall back to the normal permission flow ("ask").
    /// </summary>
    [HttpPost("permission")]
    public async Task<IActionResult> RequestPermission(string id, [FromBody] PermissionBody body, CancellationToken ct)
    {
        var rec = await AuthAsync(id, ct);
        if (rec is null) return Unauthorized();

        var req = new PermissionRequest
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            SessionId = id, Owner = rec.Owner,
            Tool = string.IsNullOrWhiteSpace(body.Tool) ? "a tool" : body.Tool.Trim(),
            Summary = body.Input
        };
        await _permissions.CreateAsync(req, ct);
        if (!await _permNotifier.PostAsync(req, ct))
        {
            await _permissions.DeleteAsync(req.Id, ct);   // no out-of-band approver → normal flow
            return Ok(new { decision = "ask" });
        }
        return Ok(new { id = req.Id });
    }

    /// <summary>Polled by the hook: returns "allow" | "allowAlways" | "deny" | "pending".</summary>
    [HttpGet("permission/{reqId}")]
    public async Task<IActionResult> PermissionStatus(string id, string reqId, CancellationToken ct)
    {
        if (await AuthAsync(id, ct) is null) return Unauthorized();
        return Ok(new { decision = await _permissions.GetDecisionAsync(reqId, ct) ?? "pending" });
    }

    /// <summary>Evaluates the live MCP restriction policy for this session.</summary>
    [HttpPost("mcp-policy")]
    public async Task<IActionResult> McpPolicy(string id, [FromBody] PermissionBody body, CancellationToken ct)
    {
        if (await AuthAsync(id, ct) is null) return Unauthorized();
        var policy = await _shares.GetMcpPolicyAsync(id, ct);
        var blocked = policy is not null && McpPolicyMatcher.IsBlocked(
            body.Tool ?? string.Empty, policy.BlockedServers, policy.BlockedTools);
        return Ok(new { decision = blocked ? "deny" : "allow" });
    }
    /// <summary>Mints a presigned PUT URL so the agent can upload an artifact to S3.</summary>
    [HttpPost("artifact-url")]
    public async Task<IActionResult> ArtifactUrl(string id, [FromQuery] string name, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Agent-Token", out var tok)) return Unauthorized();
        var url = await _svc.MintArtifactUploadUrlAsync(id, tok!, name, ct);
        return url is null ? Unauthorized() : Ok(new { url });
    }
}
