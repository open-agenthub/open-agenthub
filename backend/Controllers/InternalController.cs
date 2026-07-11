using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
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
    private readonly INotifier _notifier;
    private readonly ISessionService _svc;

    public InternalController(ISessionStore store, INotifier notifier, ISessionService svc)
    {
        _store = store; _notifier = notifier; _svc = svc;
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
            await _notifier.NotifyAsync(rec, body.Status.ToLowerInvariant(),
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
        await _notifier.NotifyAsync(rec, body.Event ?? "question",
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

    /// <summary>Mints a presigned PUT URL so the agent can upload an artifact to S3.</summary>
    [HttpPost("artifact-url")]
    public async Task<IActionResult> ArtifactUrl(string id, [FromQuery] string name, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Agent-Token", out var tok)) return Unauthorized();
        var url = await _svc.MintArtifactUploadUrlAsync(id, tok!, name, ct);
        return url is null ? Unauthorized() : Ok(new { url });
    }
}
