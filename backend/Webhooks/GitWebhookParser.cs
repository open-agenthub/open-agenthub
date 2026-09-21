using System.Text.Json;

namespace AgentHub.Api.Webhooks;

/// <summary>Provider-neutral view of a merge-request / pull-request webhook event.</summary>
public sealed record GitWebhookEvent
{
    /// <summary>"merge_request" (GitLab) or "pull_request" (GitHub).</summary>
    public required string Kind { get; init; }
    /// <summary>Normalized action: "opened", "reopened", "updated", "closed", "merged", ….</summary>
    public required string Action { get; init; }
    /// <summary>MR iid / PR number.</summary>
    public required long Number { get; init; }
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string SourceBranch { get; init; } = "";
    public string TargetBranch { get; init; } = "";
    /// <summary>Web URL of the MR/PR.</summary>
    public string Url { get; init; } = "";
    /// <summary>"group/repo" (GitLab path_with_namespace / GitHub full_name).</summary>
    public string RepoFullName { get; init; } = "";
    public string CloneUrl { get; init; } = "";
}

/// <summary>
/// Parses GitLab merge_request hooks and GitHub pull_request events into
/// <see cref="GitWebhookEvent"/>. Any other payload (push, issues, ping, …)
/// returns null and is ignored by the delivery endpoint.
/// </summary>
public static class GitWebhookParser
{
    public static GitWebhookEvent? Parse(string body)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("object_kind", out var kind)
                && kind.GetString() == "merge_request") return ParseGitLab(root);
            if (root.TryGetProperty("pull_request", out var pr)
                && pr.ValueKind == JsonValueKind.Object) return ParseGitHub(root, pr);
            return null;
        }
    }

    private static GitWebhookEvent? ParseGitLab(JsonElement root)
    {
        if (!root.TryGetProperty("object_attributes", out var attrs)
            || attrs.ValueKind != JsonValueKind.Object) return null;
        root.TryGetProperty("project", out var project);
        return new GitWebhookEvent
        {
            Kind = "merge_request",
            Action = NormalizeGitLabAction(Str(attrs, "action")),
            Number = attrs.TryGetProperty("iid", out var iid) && iid.TryGetInt64(out var n) ? n : 0,
            Title = Str(attrs, "title"),
            Description = Str(attrs, "description"),
            SourceBranch = Str(attrs, "source_branch"),
            TargetBranch = Str(attrs, "target_branch"),
            Url = Str(attrs, "url"),
            RepoFullName = Str(project, "path_with_namespace"),
            CloneUrl = Str(project, "git_http_url")
        };
    }

    private static GitWebhookEvent ParseGitHub(JsonElement root, JsonElement pr)
    {
        root.TryGetProperty("repository", out var repo);
        return new GitWebhookEvent
        {
            Kind = "pull_request",
            Action = Str(root, "action").ToLowerInvariant(),
            Number = root.TryGetProperty("number", out var n) && n.TryGetInt64(out var v) ? v
                : pr.TryGetProperty("number", out var pn) && pn.TryGetInt64(out var pv) ? pv : 0,
            Title = Str(pr, "title"),
            Description = Str(pr, "body"),
            SourceBranch = pr.TryGetProperty("head", out var head) ? Str(head, "ref") : "",
            TargetBranch = pr.TryGetProperty("base", out var @base) ? Str(@base, "ref") : "",
            Url = Str(pr, "html_url"),
            RepoFullName = Str(repo, "full_name"),
            CloneUrl = Str(repo, "clone_url")
        };
    }

    /// <summary>GitLab uses "open"/"reopen"/…; map onto GitHub's past-tense names so a
    /// trigger's event list works for both providers.</summary>
    private static string NormalizeGitLabAction(string action) => action switch
    {
        "open" => "opened",
        "reopen" => "reopened",
        "update" => "updated",
        "close" => "closed",
        "merge" => "merged",
        _ => action.ToLowerInvariant()
    };

    private static string Str(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
