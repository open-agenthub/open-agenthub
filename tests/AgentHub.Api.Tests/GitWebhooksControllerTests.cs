using System.Security.Cryptography;
using System.Text;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Webhooks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class GitWebhooksControllerTests
{
    private const string Secret = "whs_1234567890abcdef1234567890abcdef";
    private static readonly PlainWebhookSecretProtector Protector = new();

    // ------------------------------------------------------------------ GitLab

    [Fact]
    public async Task GitLabOpened_WithValidToken_StartsSessionForTriggerOwner()
    {
        var (store, trigger) = Store(promptTemplate: "Review {{repo}} MR {{id}}: {{title}} ({{url}})",
            providerId: "gitlab", projectId: "proj-1", autoApprove: true, agent: AgentKind.Codex);
        var sessions = new RecordingWebhookSessionService();
        var controller = Controller(store, sessions);
        SetGitLabRequest(controller, WebhookTestPayloads.GitLabMergeRequestOpened, Secret);

        var result = await controller.Deliver(trigger.Info.Id, CancellationToken.None);

        AssertStatus(result, "started");
        var (owner, req) = Assert.Single(sessions.Created);
        Assert.Equal("alice", owner);
        Assert.Equal(SessionMode.Autonomous, req.Mode);
        Assert.Equal("MR !12: Add rate limiter", req.Title);
        Assert.Equal("Review group/demo MR 12: Add rate limiter " +
            "(https://gitlab.example.test/group/demo/-/merge_requests/12)", req.Prompt);
        var repo = Assert.Single(req.Repos);
        Assert.Equal("https://gitlab.example.test/group/demo.git", repo.Url);
        Assert.Equal("feat/rate-limit", repo.Branch);
        Assert.Equal("gitlab", repo.ProviderId);
        Assert.Equal("proj-1", req.ProjectId);
        Assert.Equal(AgentKind.Codex, req.Agent);
        Assert.True(req.AutoApprove);
        Assert.Equal(1, store.TouchCalls);
        Assert.Equal(trigger.Info.Id, store.LastTouchedId);
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData(null)]
    public async Task GitLab_WrongOrMissingToken_Returns401AndNoSession(string? token)
    {
        var (store, trigger) = Store();
        var sessions = new RecordingWebhookSessionService();
        var controller = Controller(store, sessions);
        SetGitLabRequest(controller, WebhookTestPayloads.GitLabMergeRequestOpened, token);

        var result = await controller.Deliver(trigger.Info.Id, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(sessions.Created);
        Assert.Equal(0, store.TouchCalls);
    }

    // ------------------------------------------------------------------ GitHub

    [Fact]
    public async Task GitHubOpened_WithValidSignature_StartsSession()
    {
        var (store, trigger) = Store(providerId: "github");
        var sessions = new RecordingWebhookSessionService();
        var controller = Controller(store, sessions);
        SetGitHubRequest(controller, WebhookTestPayloads.GitHubPullRequestOpened, Secret);

        var result = await controller.Deliver(trigger.Info.Id, CancellationToken.None);

        AssertStatus(result, "started");
        var (owner, req) = Assert.Single(sessions.Created);
        Assert.Equal("alice", owner);
        Assert.Equal("PR #7: Fix login redirect", req.Title);
        var repo = Assert.Single(req.Repos);
        Assert.Equal("https://github.com/octo/demo.git", repo.Url);
        Assert.Equal("fix/login-redirect", repo.Branch);
        Assert.Equal("github", repo.ProviderId);
    }

    [Fact]
    public async Task GitHub_InvalidSignature_Returns401()
    {
        var (store, trigger) = Store();
        var sessions = new RecordingWebhookSessionService();
        var controller = Controller(store, sessions);
        SetGitHubRequest(controller, WebhookTestPayloads.GitHubPullRequestOpened, "other-secret");

        var result = await controller.Deliver(trigger.Info.Id, CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(sessions.Created);
    }

    // ------------------------------------------------------------------ filtering

    [Fact]
    public async Task UnsubscribedAction_IsIgnoredWithoutSession()
    {
        var (store, trigger) = Store(events: ["opened", "reopened"]);
        var sessions = new RecordingWebhookSessionService();
        var controller = Controller(store, sessions);
        SetGitLabRequest(controller, WebhookTestPayloads.GitLabMergeRequestClosed, Secret);

        var result = await controller.Deliver(trigger.Info.Id, CancellationToken.None);

        AssertStatus(result, "ignored");
        Assert.Empty(sessions.Created);
    }

    [Fact]
    public async Task SubscribedNonDefaultAction_StartsSession()
    {
        var (store, trigger) = Store(events: ["closed"]);
        var sessions = new RecordingWebhookSessionService();
        var controller = Controller(store, sessions);
        SetGitLabRequest(controller, WebhookTestPayloads.GitLabMergeRequestClosed, Secret);

        var result = await controller.Deliver(trigger.Info.Id, CancellationToken.None);

        AssertStatus(result, "started");
        Assert.Single(sessions.Created);
    }

    [Theory]
    [InlineData(WebhookTestPayloads.GitLabPush)]
    [InlineData(WebhookTestPayloads.GitHubPing)]
    public async Task NonMergeRequestEvent_IsIgnored(string payload)
    {
        var (store, trigger) = Store();
        var sessions = new RecordingWebhookSessionService();
        var controller = Controller(store, sessions);
        SetGitLabRequest(controller, payload, Secret);

        var result = await controller.Deliver(trigger.Info.Id, CancellationToken.None);

        AssertStatus(result, "ignored");
        Assert.Empty(sessions.Created);
    }

    [Fact]
    public async Task RepoFilterMismatch_IsIgnored()
    {
        var (store, trigger) = Store(repoFilter: "another-group/");
        var sessions = new RecordingWebhookSessionService();
        var controller = Controller(store, sessions);
        SetGitLabRequest(controller, WebhookTestPayloads.GitLabMergeRequestOpened, Secret);

        var result = await controller.Deliver(trigger.Info.Id, CancellationToken.None);

        AssertStatus(result, "ignored");
        Assert.Empty(sessions.Created);
    }

    [Fact]
    public async Task RepoFilterMatch_IsCaseInsensitive()
    {
        var (store, trigger) = Store(repoFilter: "GROUP/DEMO");
        var sessions = new RecordingWebhookSessionService();
        var controller = Controller(store, sessions);
        SetGitLabRequest(controller, WebhookTestPayloads.GitLabMergeRequestOpened, Secret);

        var result = await controller.Deliver(trigger.Info.Id, CancellationToken.None);

        AssertStatus(result, "started");
        Assert.Single(sessions.Created);
    }

    // ------------------------------------------------------------------ dedup & errors

    [Fact]
    public async Task RepeatedDelivery_StartsOnlyOneSession()
    {
        var (store, trigger) = Store();
        var sessions = new RecordingWebhookSessionService();
        var dedup = new WebhookDeduplicator();
        var first = Controller(store, sessions, dedup);
        SetGitLabRequest(first, WebhookTestPayloads.GitLabMergeRequestOpened, Secret);
        var second = Controller(store, sessions, dedup);
        SetGitLabRequest(second, WebhookTestPayloads.GitLabMergeRequestOpened, Secret);

        AssertStatus(await first.Deliver(trigger.Info.Id, CancellationToken.None), "started");
        AssertStatus(await second.Deliver(trigger.Info.Id, CancellationToken.None), "duplicate");
        Assert.Single(sessions.Created);
    }

    [Fact]
    public async Task UnknownTrigger_Returns404()
    {
        var controller = Controller(new InMemoryWebhookTriggerStore(), new RecordingWebhookSessionService());
        SetGitLabRequest(controller, WebhookTestPayloads.GitLabMergeRequestOpened, Secret);

        Assert.IsType<NotFoundResult>(await controller.Deliver("missing", CancellationToken.None));
    }

    [Fact]
    public async Task SessionLimitExceeded_Returns200ErrorInsteadOfRetryableFailure()
    {
        var (store, trigger) = Store();
        var sessions = new RecordingWebhookSessionService
        {
            CreateException = new SessionLimitExceededException("Running session limit reached.")
        };
        var controller = Controller(store, sessions);
        SetGitLabRequest(controller, WebhookTestPayloads.GitLabMergeRequestOpened, Secret);

        var result = await controller.Deliver(trigger.Info.Id, CancellationToken.None);

        AssertStatus(result, "error");
        Assert.Equal(0, store.TouchCalls);
    }

    // ------------------------------------------------------------------ fixtures

    private static (InMemoryWebhookTriggerStore Store, WebhookTriggerRecord Trigger) Store(
        string promptTemplate = "Work on {{title}}", string? providerId = null,
        List<string>? events = null, string? repoFilter = null, string? projectId = null,
        bool autoApprove = false, AgentKind agent = AgentKind.Claude)
    {
        var store = new InMemoryWebhookTriggerStore();
        store.CreateAsync("alice", new CreateWebhookTriggerRequest
        {
            Name = "review-bot",
            ProviderId = providerId,
            Events = events ?? ["opened", "reopened"],
            RepoFilter = repoFilter,
            PromptTemplate = promptTemplate,
            ProjectId = projectId,
            Agent = agent,
            AutoApprove = autoApprove
        }, Protector.Protect(Secret)).GetAwaiter().GetResult();
        return (store, store.Records[0]);
    }

    private static GitWebhooksController Controller(IWebhookTriggerStore store,
        ISessionService sessions, WebhookDeduplicator? dedup = null)
        => new(store, Protector, sessions, dedup ?? new WebhookDeduplicator(),
            NullLogger<GitWebhooksController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static void SetBody(ControllerBase controller, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        controller.Request.Body = new MemoryStream(bytes);
        controller.Request.ContentLength = bytes.Length;
        controller.Request.ContentType = "application/json";
    }

    private static void SetGitLabRequest(ControllerBase controller, string payload, string? token)
    {
        SetBody(controller, payload);
        controller.Request.Headers["X-Gitlab-Event"] = "Merge Request Hook";
        if (token is not null) controller.Request.Headers["X-Gitlab-Token"] = token;
    }

    private static void SetGitHubRequest(ControllerBase controller, string payload, string signingSecret)
    {
        SetBody(controller, payload);
        controller.Request.Headers["X-GitHub-Event"] = "pull_request";
        controller.Request.Headers["X-Hub-Signature-256"] = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret),
                Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static void AssertStatus(IActionResult result, string expected)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        var status = ok.Value!.GetType().GetProperty("status")?.GetValue(ok.Value) as string;
        Assert.Equal(expected, status);
    }
}
