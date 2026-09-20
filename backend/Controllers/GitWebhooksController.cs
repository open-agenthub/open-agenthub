using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Public delivery endpoint for webhook triggers: GitLab merge_request hooks and
/// GitHub pull_request events start an autonomous session under the trigger owner's
/// account. Anonymous by design — the caller is GitLab/GitHub, authenticated per
/// trigger via the shared secret (GitLab token header / GitHub HMAC signature).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/git/webhooks")]
public sealed class GitWebhooksController : ControllerBase
{
    /// <summary>Real MR/PR payloads are tens of KB; anything bigger is not for us.</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    private readonly IWebhookTriggerStore _store;
    private readonly IWebhookSecretProtector _secrets;
    private readonly ISessionService _sessions;
    private readonly WebhookDeduplicator _dedup;
    private readonly ILogger<GitWebhooksController> _log;

    public GitWebhooksController(IWebhookTriggerStore store, IWebhookSecretProtector secrets,
        ISessionService sessions, WebhookDeduplicator dedup, ILogger<GitWebhooksController> log)
    { _store = store; _secrets = secrets; _sessions = sessions; _dedup = dedup; _log = log; }

    [HttpPost("{triggerId}")]
    public async Task<IActionResult> Deliver(string triggerId, CancellationToken ct)
    {
        var trigger = await _store.GetByIdAsync(triggerId, ct);
        if (trigger is null) return NotFound();

        var body = await ReadBodyAsync(ct);
        if (body is null) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        // Signature/token check comes before any payload parsing. GitHub signs the raw
        // body; GitLab echoes the secret — either proof of the shared secret is accepted.
        var secret = _secrets.Unprotect(trigger.SecretProtected);
        var githubSignature = Request.Headers[WebhookSignatureVerifier.GitHubSignatureHeader].FirstOrDefault();
        var authentic = githubSignature is not null
            ? WebhookSignatureVerifier.VerifyGitHubSignature(githubSignature, body, secret)
            : WebhookSignatureVerifier.VerifyGitLabToken(
                Request.Headers[WebhookSignatureVerifier.GitLabTokenHeader].FirstOrDefault(), secret);
        if (!authentic) return Unauthorized();

        var evt = GitWebhookParser.Parse(Encoding.UTF8.GetString(body));
        if (evt is null) return Ignored("not a merge request / pull request event");

        var events = trigger.Info.Events.Count > 0 ? trigger.Info.Events : WebhookTriggerEvents.Default;
        if (!events.Contains(evt.Action)) return Ignored($"action '{evt.Action}' is not subscribed");
        if (!MatchesRepoFilter(trigger.Info.RepoFilter, evt)) return Ignored("repository does not match the filter");
        if (string.IsNullOrWhiteSpace(evt.CloneUrl)) return Ignored("payload carries no clone URL");

        if (!_dedup.TryBegin($"{triggerId}:{evt.Kind}:{evt.Number}:{evt.Action}"))
            return Ok(new { status = "duplicate" });

        var request = new CreateSessionRequest
        {
            Title = TitleFor(evt),
            Mode = SessionMode.Autonomous,
            Prompt = WebhookPromptTemplate.Render(trigger.Info.PromptTemplate, evt),
            Repos =
            [
                new RepoRef
                {
                    Url = evt.CloneUrl,
                    Branch = evt.SourceBranch.Length > 0 ? evt.SourceBranch : null,
                    ProviderId = trigger.Info.ProviderId
                }
            ],
            ProjectId = trigger.Info.ProjectId,
            Agent = trigger.Info.Agent,
            AutoApprove = trigger.Info.AutoApprove
        };

        try
        {
            var info = await _sessions.CreateSessionAsync(trigger.Owner, request, ct);
            await _store.TouchAsync(triggerId, ct);
            return Ok(new { status = "started", sessionId = info.Id });
        }
        catch (Exception e) when (e is ArgumentException or SessionLimitExceededException
                                    or Usage.UsageLimitExceededException or InvalidOperationException)
        {
            // 200, not 5xx: providers retry failed deliveries, and a retry would only
            // re-hit the same limit (the dedup window already absorbs quick retries).
            _log.LogWarning("Webhook trigger {Trigger} could not start a session: {Message}",
                triggerId, e.Message);
            return Ok(new { status = "error", message = e.Message });
        }
    }

    private OkObjectResult Ignored(string reason) => Ok(new { status = "ignored", reason });

    private static bool MatchesRepoFilter(string? filter, GitWebhookEvent evt)
        => string.IsNullOrWhiteSpace(filter)
           || evt.RepoFullName.Contains(filter, StringComparison.OrdinalIgnoreCase)
           || evt.CloneUrl.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string TitleFor(GitWebhookEvent evt)
    {
        var label = evt.Kind == "pull_request" ? $"PR #{evt.Number}" : $"MR !{evt.Number}";
        var title = evt.Title.Trim();
        if (title.Length > 80) title = title[..79] + "…";
        return title.Length == 0 ? label : $"{label}: {title}";
    }

    /// <summary>Raw body bytes (needed verbatim for the HMAC), or null when oversized.</summary>
    private async Task<byte[]?> ReadBodyAsync(CancellationToken ct)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
