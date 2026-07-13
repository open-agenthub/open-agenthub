using System.Security.Claims;
using AgentHub.Api.Ee.Slack;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Per-user Slack preferences: connect (auto-resolved via the login email → DM),
/// override the target conversation, or opt out. No global hard-coded channel.
/// </summary>
[ApiController]
[Authorize]
[Route("api/slack")]
public sealed class SlackController : ControllerBase
{
    private readonly SlackOptions _opts;
    private readonly UserDirectory _dir;
    private readonly ISlackTargetResolver _resolver;

    public SlackController(SlackOptions opts, UserDirectory dir, ISlackTargetResolver resolver)
    { _opts = opts; _dir = dir; _resolver = resolver; }

    private string Owner =>
        User.FindFirstValue("preferred_username") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "dev";

    public sealed record SlackMe(bool Configured, bool Enabled, string? ChannelOverride, string? Email, bool Connected);
    public sealed record SlackPrefs(bool Enabled, string? ChannelOverride);

    [HttpGet("me")]
    public async Task<SlackMe> Me(CancellationToken ct)
    {
        if (!_opts.Enabled)
            return new SlackMe(false, false, null, null, false);
        var user = await _dir.GetAsync(Owner, ct);
        var target = await _resolver.ResolveAsync(Owner, ct);
        return new SlackMe(true, user?.SlackEnabled ?? true, user?.SlackChannelOverride, user?.Email, target is not null);
    }

    [HttpPut("me")]
    public async Task<IActionResult> Update([FromBody] SlackPrefs prefs, CancellationToken ct)
    {
        await _dir.SetSlackPrefsAsync(Owner, prefs.Enabled, prefs.ChannelOverride, ct);
        _resolver.Invalidate(Owner);
        return NoContent();
    }
}
