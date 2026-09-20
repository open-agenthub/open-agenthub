namespace AgentHub.Api.Webhooks;

/// <summary>Normalized MR/PR actions a trigger can subscribe to (provider-neutral,
/// see <see cref="GitWebhookParser"/> for the GitLab→GitHub action mapping).</summary>
public static class WebhookTriggerEvents
{
    public static readonly IReadOnlyList<string> Default = ["opened", "reopened"];
    public static readonly IReadOnlyList<string> Allowed = ["opened", "reopened", "updated", "closed", "merged"];

    /// <summary>Trimmed, lower-cased, de-duplicated; throws on unknown actions.
    /// Empty input falls back to <see cref="Default"/>.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? events)
    {
        var normalized = (events ?? [])
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .Distinct()
            .ToList();
        if (normalized.Count == 0) return Default;
        var unknown = normalized.FirstOrDefault(e => !Allowed.Contains(e));
        if (unknown is not null)
            throw new ArgumentException(
                $"Unknown event '{unknown}'. Allowed: {string.Join(", ", Allowed)}.");
        return normalized;
    }
}
