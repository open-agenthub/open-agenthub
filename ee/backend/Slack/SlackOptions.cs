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
    /// <summary>Target channel id (e.g. C0123ABCD) the threads are posted into.</summary>
    public string Channel { get; set; } = "";

    /// <summary>Outbound notifications possible (bot token + channel present).</summary>
    public bool CanPost => Enabled && !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(Channel);
    /// <summary>Inbound replies possible (Socket Mode app token present too).</summary>
    public bool CanReceive => CanPost && !string.IsNullOrWhiteSpace(AppToken);
}
