using System.ComponentModel.DataAnnotations;

namespace AgentHub.Api.Models;

public sealed record ContextClearRequest([Required, StringLength(16_000, MinimumLength = 1)] string FollowUp);
public sealed record ContextClearOutcome(bool Scheduled);
