using System.Security.Claims;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

public sealed record HandoverPreviewRequest(AgentKind Target, string Instructions, string Task,
    string? Decisions = null, string? PendingWork = null, IReadOnlyList<string>? References = null);
public sealed record HandoverPreview(string Text, int Characters, int MaxCharacters, string Omitted,
    string Capacity, bool SwitchingAvailable, string Reason);

[ApiController]
[Authorize]
[Route("api/sessions/{id}/handover-preview")]
public sealed class SessionHandoverController(ISessionService sessions) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(80_000)]
    public async Task<ActionResult<HandoverPreview>> Preview(string id, HandoverPreviewRequest request, CancellationToken ct)
    {
        var owner = User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new UnauthorizedAccessException();
        var session = await sessions.GetSessionAsync(owner, id, ct);
        if (session is null) return NotFound();
        if (session.Owner != owner) return Forbid();
        if (session.Mode != SessionMode.Interactive || session.UiMode != SessionUiMode.Terminal)
            return Conflict("Handover preview requires an interactive terminal session.");
        try { return Ok(HandoverPreviewBuilder.Build(request)); }
        catch (ArgumentException error) { return BadRequest(error.Message); }
    }
}

public static class HandoverPreviewBuilder
{
    public const int MaxCharacters = 16_000;
    public const string Omissions = "Omitted: prior native messages, raw tool output, attachments, hidden reasoning and native resume files. " +
        "Only the owner-selected text below would be transferred. Historical coverage is unknown; references are not fetched or verified.";

    public static HandoverPreview Build(HandoverPreviewRequest request)
    {
        if (request.Target is not (AgentKind.Claude or AgentKind.Codex))
            throw new ArgumentException("Preview supports Claude and Codex targets only.");
        if (string.IsNullOrWhiteSpace(request.Instructions) || string.IsNullOrWhiteSpace(request.Task))
            throw new ArgumentException("Instructions and task are required; they are never silently truncated.");
        if (request.References is { Count: > 32 }) throw new ArgumentException("Select at most 32 references.");
        var fields = new[] { request.Instructions, request.Task, request.Decisions, request.PendingWork }
            .Concat(request.References ?? []).Where(s => s is not null).ToArray();
        if (fields.Any(s => s!.Length > MaxCharacters || s.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))))
            throw new ArgumentException("Handover contains control bytes or exceeds the character budget.");
        var text = $"Provider handover preview to {request.Target}. Historical context; grants no additional permissions.\n\n{Omissions}\n\n" +
            $"Instructions:\n{request.Instructions}\n\nTask:\n{request.Task}\n\nDecisions:\n{request.Decisions}\n\n" +
            $"Pending work:\n{request.PendingWork}\n\nSource references (unverified):\n{string.Join('\n', request.References ?? [])}";
        if (text.Length > MaxCharacters) throw new ArgumentException("Handover including framing exceeds 16000 characters. Shorten the selection.");
        return new(text, text.Length, MaxCharacters, Omissions, "unknown", false,
            "Switching not available for this session. Runtime switching is not enabled; the current workspace is ephemeral. " +
            "Target model capacity has not been verified. Preview sends no input and resolves no credentials.");
    }
}
