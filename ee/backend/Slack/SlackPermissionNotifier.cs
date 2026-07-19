// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Licensing;
using AgentHub.Api.Permissions;

namespace AgentHub.Api.Ee.Slack;

/// <summary>
/// Posts a tool-permission request to the user's Slack conversation as an interactive
/// message with Allow / Allow-always / Deny buttons. The button click is handled by
/// <see cref="SlackSocketModeService"/>. Returns false when the user has no Slack
/// target (or Slack/license is off), so the caller falls back to the normal prompt.
/// </summary>
public sealed class SlackPermissionNotifier : IPermissionNotifier, IPermissionPromptEditor
{
    public string Platform => "slack";

    private readonly SlackOptions _opts;
    private readonly IEnterpriseLicense _license;
    private readonly SlackClient _slack;
    private readonly ISlackTargetResolver _resolver;
    private readonly PermissionStore _store;
    private readonly SlackThreadStore _threads;

    public SlackPermissionNotifier(SlackOptions opts, IEnterpriseLicense license, SlackClient slack,
        ISlackTargetResolver resolver, PermissionStore store, SlackThreadStore threads)
    { _opts = opts; _license = license; _slack = slack; _resolver = resolver; _store = store; _threads = threads; }

    public async Task<bool> PostAsync(PermissionRequest req, CancellationToken ct = default)
    {
        if (!_opts.CanPost || !_license.Enabled) return false;

        // Post the request into the session's Slack thread so the approval sits right
        // under the conversation it belongs to. Fall back to the owner's DM (top-level)
        // if the session doesn't have a thread yet.
        var thread = await _threads.GetBySessionAsync(req.SessionId, ct);
        var channel = thread?.Channel ?? await _resolver.ResolveAsync(req.Owner, ct);
        var threadTs = thread?.ThreadTs;
        if (channel is null) return false;

        var summary = string.IsNullOrWhiteSpace(req.Summary) ? "" : $"\n> {Escape(Trim(req.Summary!, 600))}";
        var text = $":lock: *Open AgentHub* — the agent wants to use *{Escape(req.Tool)}*.{summary}";
        object[] blocks =
        {
            new { type = "section", text = new { type = "mrkdwn", text } },
            new
            {
                type = "actions",
                block_id = $"perm:{req.Id}",
                elements = new object[]
                {
                    new { type = "button", style = "primary", text = new { type = "plain_text", text = "Allow" },
                          action_id = PermissionAction.Id("allow", req.Id), value = req.Id },
                    new { type = "button", text = new { type = "plain_text", text = "Allow (don't ask again)" },
                          action_id = PermissionAction.Id("allowAlways", req.Id), value = req.Id },
                    new { type = "button", style = "danger", text = new { type = "plain_text", text = "Deny" },
                          action_id = PermissionAction.Id("deny", req.Id), value = req.Id }
                }
            }
        };

        var ts = await _slack.PostBlocksAsync(channel, $"Permission request: {req.Tool}", blocks, threadTs, ct);
        if (ts is null) return false;
        await _store.SetPromptMessageAsync(req.Id, "slack", channel, ts, ct);
        return true;
    }

    /// <summary>The hook stopped polling: drop the buttons so the prompt doesn't look alive.</summary>
    public async Task MarkExpiredAsync(PermissionRequest req, CancellationToken ct = default)
    {
        if (req.Channel is null || req.MessageTs is null) return;
        await _slack.UpdateMessageAsync(req.Channel, req.MessageTs,
            $":hourglass: *Expired* — *{Escape(req.Tool)}*. Please answer in the web terminal.", null, ct);
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + " …";
}
