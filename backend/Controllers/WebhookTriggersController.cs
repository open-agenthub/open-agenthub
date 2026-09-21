using System.Security.Claims;
using System.Security.Cryptography;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Webhooks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Management of webhook triggers: inbound GitLab/GitHub webhooks that start
/// autonomous sessions (delivery endpoint: <see cref="GitWebhooksController"/>).
/// The shared secret is generated server-side, stored encrypted, and returned
/// exactly once at creation together with the ready-to-paste webhook URL.
/// </summary>
[ApiController]
[Authorize]
[Route("api/webhook-triggers")]
public sealed class WebhookTriggersController : ControllerBase
{
    private readonly IWebhookTriggerStore _store;
    private readonly IWebhookSecretProtector _secrets;
    private readonly IConfiguration _cfg;

    public WebhookTriggersController(IWebhookTriggerStore store, IWebhookSecretProtector secrets,
        IConfiguration cfg)
    { _store = store; _secrets = secrets; _cfg = cfg; }

    private string Owner =>
        User.FindFirstValue("preferred_username")
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException();

    // Deliveries arrive on the public host; like the OAuth callback this must be the
    // ingress origin, not the pod-local Request.Scheme/Host (see GitController).
    private string DeliveryUrl(string triggerId)
    {
        var origin = (_cfg["FrontendOrigin"] ?? $"{Request.Scheme}://{Request.Host}").TrimEnd('/');
        return $"{origin}/api/git/webhooks/{triggerId}";
    }

    [HttpGet]
    public async Task<IReadOnlyList<WebhookTriggerInfo>> List(CancellationToken ct)
        => (await _store.ListAsync(Owner, ct))
            .Select(t => t with { Url = DeliveryUrl(t.Id) }).ToList();

    /// <summary>Creates a trigger; the response carries the plaintext secret exactly once.</summary>
    [HttpPost]
    public async Task<ActionResult<CreatedWebhookTrigger>> Create(
        [FromBody] CreateWebhookTriggerRequest req, CancellationToken ct)
    {
        var name = req.Name.Trim();
        if (name.Length == 0) return BadRequest("Name is required.");
        if (name.Length > 100) return BadRequest("Name is too long.");
        if (string.IsNullOrWhiteSpace(req.PromptTemplate)) return BadRequest("Prompt template is required.");
        if (req.PromptTemplate.Length > 10_000) return BadRequest("Prompt template is too long.");
        if (!Enum.IsDefined(req.Agent)) return BadRequest("Unsupported agent kind.");

        IReadOnlyList<string> events;
        try { events = WebhookTriggerEvents.Normalize(req.Events); }
        catch (ArgumentException e) { return BadRequest(e.Message); }

        var secret = GenerateSecret();
        var info = await _store.CreateAsync(Owner,
            req with { Name = name, Events = events.ToList() }, _secrets.Protect(secret), ct);
        return Ok(new CreatedWebhookTrigger(info with { Url = DeliveryUrl(info.Id) }, secret));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
        => await _store.DeleteAsync(Owner, id, ct) ? NoContent() : NotFound();

    /// <summary>Format: "whs_" + 32 random bytes as lowercase hex.</summary>
    private static string GenerateSecret()
        => "whs_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
