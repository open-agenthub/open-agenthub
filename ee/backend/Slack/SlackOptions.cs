// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
namespace AgentHub.Api.Ee.Slack;

/// <summary>Configuration for the Slack integration (section "Ee:Slack").</summary>
public sealed class SlackOptions
{
    public bool Enabled { get; set; }
    /// <summary>Bot token (xoxb-…) — used for chat.postMessage.</summary>
    public string BotToken { get; set; } = "";
    /// <summary>App-level token (xapp-…) — used for Socket Mode.</summary>
    public string AppToken { get; set; } = "";
    /// <summary>Optional fallback channel id (e.g. C0123ABCD). Normally the target is
    /// resolved per user (their Slack DM); this is only used when a user has no email
    /// and no override. Leave empty to skip notifying such users.</summary>
    public string Channel { get; set; } = "";

    /// <summary>Outbound notifications possible (enabled + bot token). The concrete
    /// target channel is resolved per user at send time.</summary>
    public bool CanPost => Enabled && !string.IsNullOrWhiteSpace(BotToken);
    /// <summary>Inbound replies possible (Socket Mode app token present too).</summary>
    public bool CanReceive => CanPost && !string.IsNullOrWhiteSpace(AppToken);
}
