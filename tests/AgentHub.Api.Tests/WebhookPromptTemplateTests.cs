using AgentHub.Api.Webhooks;
using Xunit;

namespace AgentHub.Api.Tests;

public class WebhookPromptTemplateTests
{
    private static readonly GitWebhookEvent Event = new()
    {
        Kind = "merge_request",
        Action = "opened",
        Number = 12,
        Title = "Add rate limiter",
        Description = "Please review.",
        SourceBranch = "feat/rate-limit",
        TargetBranch = "main",
        Url = "https://gitlab.example.test/group/demo/-/merge_requests/12",
        RepoFullName = "group/demo",
        CloneUrl = "https://gitlab.example.test/group/demo.git"
    };

    [Fact]
    public void Render_ReplacesAllPlaceholders()
    {
        const string template = "Review {{repo}} {{id}} ({{action}}): {{title}}\n" +
            "{{description}}\nBranch {{source_branch}} into {{target_branch}} — {{url}}";

        var rendered = WebhookPromptTemplate.Render(template, Event);

        Assert.Equal("Review group/demo 12 (opened): Add rate limiter\n" +
            "Please review.\nBranch feat/rate-limit into main — " +
            "https://gitlab.example.test/group/demo/-/merge_requests/12", rendered);
    }

    [Fact]
    public void Render_LeavesUnknownPlaceholdersUntouched()
        => Assert.Equal("x {{unknown}} Add rate limiter",
            WebhookPromptTemplate.Render("x {{unknown}} {{title}}", Event));

    [Fact]
    public void Render_TemplateWithoutPlaceholders_IsReturnedVerbatim()
        => Assert.Equal("Fixed prompt.", WebhookPromptTemplate.Render("Fixed prompt.", Event));
}
