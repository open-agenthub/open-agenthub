namespace AgentHub.Api.Tests;

/// <summary>Sample webhook payloads (trimmed to the fields the parser reads).</summary>
internal static class WebhookTestPayloads
{
    public const string GitLabMergeRequestOpened = """
        {
          "object_kind": "merge_request",
          "event_type": "merge_request",
          "user": { "name": "Alice" },
          "project": {
            "id": 1,
            "name": "demo",
            "path_with_namespace": "group/demo",
            "git_http_url": "https://gitlab.example.test/group/demo.git",
            "web_url": "https://gitlab.example.test/group/demo"
          },
          "object_attributes": {
            "iid": 12,
            "title": "Add rate limiter",
            "description": "Please review the new limiter.",
            "source_branch": "feat/rate-limit",
            "target_branch": "main",
            "url": "https://gitlab.example.test/group/demo/-/merge_requests/12",
            "state": "opened",
            "action": "open"
          }
        }
        """;

    public const string GitLabMergeRequestClosed = """
        {
          "object_kind": "merge_request",
          "project": {
            "path_with_namespace": "group/demo",
            "git_http_url": "https://gitlab.example.test/group/demo.git"
          },
          "object_attributes": {
            "iid": 12,
            "title": "Add rate limiter",
            "source_branch": "feat/rate-limit",
            "target_branch": "main",
            "url": "https://gitlab.example.test/group/demo/-/merge_requests/12",
            "action": "close"
          }
        }
        """;

    public const string GitLabPush = """
        { "object_kind": "push", "ref": "refs/heads/main" }
        """;

    public const string GitHubPullRequestOpened = """
        {
          "action": "opened",
          "number": 7,
          "pull_request": {
            "number": 7,
            "title": "Fix login redirect",
            "body": "Redirect loop after logout.",
            "html_url": "https://github.com/octo/demo/pull/7",
            "head": { "ref": "fix/login-redirect" },
            "base": { "ref": "main" }
          },
          "repository": {
            "full_name": "octo/demo",
            "clone_url": "https://github.com/octo/demo.git"
          }
        }
        """;

    public const string GitHubPing = """
        { "zen": "Keep it logically awesome.", "hook_id": 1 }
        """;
}
