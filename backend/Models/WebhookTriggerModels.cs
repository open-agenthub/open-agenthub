namespace AgentHub.Api.Models;

/// <summary>
/// A webhook trigger: an inbound GitLab/GitHub webhook (merge request / pull request
/// events) that starts an autonomous session under the trigger owner's account.
/// </summary>
public sealed record WebhookTriggerInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Connected Git provider whose OAuth token authenticates the clone
    /// (null = anonymous clone / public repository).</summary>
    public string? ProviderId { get; init; }
    /// <summary>Normalized MR/PR actions that start a session (e.g. "opened", "reopened").</summary>
    public IReadOnlyList<string> Events { get; init; } = Array.Empty<string>();
    /// <summary>Optional case-insensitive substring matched against the repository
    /// full name ("group/repo") and clone URL; non-matching deliveries are ignored.</summary>
    public string? RepoFilter { get; init; }
    /// <summary>Session prompt with placeholders: {{title}}, {{description}},
    /// {{source_branch}}, {{target_branch}}, {{url}}, {{repo}}, {{id}}, {{action}}.</summary>
    public required string PromptTemplate { get; init; }
    public string? ProjectId { get; init; }
    public AgentKind Agent { get; init; } = AgentKind.Claude;
    public bool AutoApprove { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastTriggeredAt { get; init; }
    /// <summary>Public delivery URL; derived by the API layer, never stored.</summary>
    public string Url { get; init; } = "";
}

public sealed record CreateWebhookTriggerRequest
{
    public string Name { get; init; } = "";
    public string? ProviderId { get; init; }
    /// <summary>Empty = default ("opened", "reopened").</summary>
    public List<string> Events { get; init; } = new();
    public string? RepoFilter { get; init; }
    public string PromptTemplate { get; init; } = "";
    public string? ProjectId { get; init; }
    public AgentKind Agent { get; init; } = AgentKind.Claude;
    public bool AutoApprove { get; init; }
}

/// <summary>Creation response; the plaintext secret is returned exactly once.</summary>
public sealed record CreatedWebhookTrigger(WebhookTriggerInfo Trigger, string Secret);
