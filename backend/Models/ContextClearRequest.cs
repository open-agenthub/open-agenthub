using System.ComponentModel.DataAnnotations;

namespace AgentHub.Api.Models;

public sealed record ContextClearRequest([Required, StringLength(16_000, MinimumLength = 1)] string FollowUp);
/// <summary>The pod's answer to a context clear request.</summary>
/// <param name="Scheduled">True once the pod has queued the clear.</param>
/// <param name="Handover">The pod's own words on when the handover arrives, passed through to the
/// tool result so the agent knows to end its turn. Null from runtimes older than #336.</param>
public sealed record ContextClearOutcome(bool Scheduled, string? Handover = null);
