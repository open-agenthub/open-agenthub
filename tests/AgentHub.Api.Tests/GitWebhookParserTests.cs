using AgentHub.Api.Webhooks;
using Xunit;

namespace AgentHub.Api.Tests;

public class GitWebhookParserTests
{
    [Fact]
    public void GitLabMergeRequest_IsParsedAndActionNormalized()
    {
        var evt = GitWebhookParser.Parse(WebhookTestPayloads.GitLabMergeRequestOpened);

        Assert.NotNull(evt);
        Assert.Equal("merge_request", evt.Kind);
        Assert.Equal("opened", evt.Action); // GitLab "open" → normalized "opened"
        Assert.Equal(12, evt.Number);
        Assert.Equal("Add rate limiter", evt.Title);
        Assert.Equal("Please review the new limiter.", evt.Description);
        Assert.Equal("feat/rate-limit", evt.SourceBranch);
        Assert.Equal("main", evt.TargetBranch);
        Assert.Equal("https://gitlab.example.test/group/demo/-/merge_requests/12", evt.Url);
        Assert.Equal("group/demo", evt.RepoFullName);
        Assert.Equal("https://gitlab.example.test/group/demo.git", evt.CloneUrl);
    }

    [Theory]
    [InlineData(WebhookTestPayloads.GitLabMergeRequestClosed, "closed")]
    public void GitLabActions_MapToGitHubNames(string payload, string expected)
    {
        var evt = GitWebhookParser.Parse(payload);
        Assert.NotNull(evt);
        Assert.Equal(expected, evt.Action);
    }

    [Fact]
    public void GitHubPullRequest_IsParsed()
    {
        var evt = GitWebhookParser.Parse(WebhookTestPayloads.GitHubPullRequestOpened);

        Assert.NotNull(evt);
        Assert.Equal("pull_request", evt.Kind);
        Assert.Equal("opened", evt.Action);
        Assert.Equal(7, evt.Number);
        Assert.Equal("Fix login redirect", evt.Title);
        Assert.Equal("Redirect loop after logout.", evt.Description);
        Assert.Equal("fix/login-redirect", evt.SourceBranch);
        Assert.Equal("main", evt.TargetBranch);
        Assert.Equal("https://github.com/octo/demo/pull/7", evt.Url);
        Assert.Equal("octo/demo", evt.RepoFullName);
        Assert.Equal("https://github.com/octo/demo.git", evt.CloneUrl);
    }

    [Fact]
    public void GitHubPullRequest_NullBody_YieldsEmptyDescription()
    {
        var evt = GitWebhookParser.Parse("""
            {
              "action": "reopened",
              "number": 3,
              "pull_request": { "title": "t", "body": null, "head": { "ref": "b" }, "base": { "ref": "main" } },
              "repository": { "full_name": "octo/demo", "clone_url": "https://github.com/octo/demo.git" }
            }
            """);
        Assert.NotNull(evt);
        Assert.Equal("", evt.Description);
        Assert.Equal("reopened", evt.Action);
    }

    [Theory]
    [InlineData(WebhookTestPayloads.GitLabPush)]
    [InlineData(WebhookTestPayloads.GitHubPing)]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    public void OtherPayloads_ReturnNull(string payload)
        => Assert.Null(GitWebhookParser.Parse(payload));
}
