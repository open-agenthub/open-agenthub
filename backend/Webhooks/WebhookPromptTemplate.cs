namespace AgentHub.Api.Webhooks;

/// <summary>Renders a trigger's prompt template against a webhook event.
/// Unknown placeholders are left untouched (no fragile "escape" syntax needed).</summary>
public static class WebhookPromptTemplate
{
    public static readonly IReadOnlyList<string> Placeholders =
        ["title", "description", "source_branch", "target_branch", "url", "repo", "id", "action"];

    public static string Render(string template, GitWebhookEvent e) => template
        .Replace("{{title}}", e.Title)
        .Replace("{{description}}", e.Description)
        .Replace("{{source_branch}}", e.SourceBranch)
        .Replace("{{target_branch}}", e.TargetBranch)
        .Replace("{{url}}", e.Url)
        .Replace("{{repo}}", e.RepoFullName)
        .Replace("{{id}}", e.Number.ToString())
        .Replace("{{action}}", e.Action);
}
