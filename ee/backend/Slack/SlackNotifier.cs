// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Licensing;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

namespace AgentHub.Api.Ee.Slack;

/// <summary>
/// Notifier that mirrors a session into a Slack thread: on every "question" event
/// it posts the new terminal output since the last update plus the question, so the
/// whole conversation accumulates in one thread. Replies are handled by
/// <see cref="SlackSocketModeService"/>. Requires a valid enterprise license.
/// </summary>
public sealed class SlackNotifier : INotifier
{
    private readonly SlackOptions _opts;
    private readonly IEnterpriseLicense _license;
    private readonly SlackClient _slack;
    private readonly SlackThreadStore _threads;
    private readonly ISlackTargetResolver _resolver;
    private readonly ISessionService _sessions;
    private readonly int _agentPort;
    private readonly string _frontendOrigin;
    private readonly ILogger<SlackNotifier> _log;

    public SlackNotifier(SlackOptions opts, IEnterpriseLicense license, SlackClient slack,
        SlackThreadStore threads, ISlackTargetResolver resolver, ISessionService sessions,
        IConfiguration cfg, ILogger<SlackNotifier> log)
    {
        _opts = opts; _license = license; _slack = slack; _threads = threads; _resolver = resolver; _sessions = sessions;
        _agentPort = cfg.GetValue("AgentHub:AgentPort", 7681);
        _frontendOrigin = cfg["FrontendOrigin"] ?? "";
        _log = log;
    }

    public async Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
    {
        if (!_opts.CanPost || !_license.Enabled) return;

        try
        {
            var thread = await _threads.GetBySessionAsync(s.Id, ct);

            if (eventType is "finished" or "failed")
            {
                if (thread is not null)
                    await _slack.PostMessageAsync(thread.Channel, $":checkered_flag: *{eventType}* — {message}", thread.ThreadTs, ct);
                return;
            }
            if (eventType != "question") return;

            // Create the thread on first contact — in the session owner's resolved
            // conversation (their Slack DM, an override, or the fallback channel).
            if (thread is null)
            {
                var channel = await _resolver.ResolveAsync(s.Owner, ct);
                if (channel is null) return; // no Slack target for this user (opted out / not found)
                var header = $":robot_face: *{Escape(s.Title)}* (`{s.Mode}`)\n" +
                             $"Session `{s.Id}` — owner `{s.Owner}`\n" +
                             (string.IsNullOrEmpty(_frontendOrigin) ? "" : $"{_frontendOrigin}/s/{s.Id}\n") +
                             "_Reply in this thread to answer the agent._";
                var ts = await _slack.PostMessageAsync(channel, header, null, ct);
                if (ts is null) return;
                thread = new SlackThread(s.Id, s.Owner, channel, ts, 0);
                await _threads.UpsertAsync(thread, ct);
            }

            // New terminal output since the last update (best-effort; needs a running pod).
            var delta = await NewOutputAsync(s, thread, ct);
            if (!string.IsNullOrWhiteSpace(delta))
                await _slack.PostMessageAsync(thread.Channel, $"```{Trim(delta, 2800)}```", thread.ThreadTs, ct);

            await _slack.PostMessageAsync(thread.Channel, $":raising_hand: {Escape(message)}", thread.ThreadTs, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Slack notify failed for session {Id}", s.Id); }
    }

    private async Task<string> NewOutputAsync(SessionRecord s, SlackThread thread, CancellationToken ct)
    {
        try
        {
            var info = await _sessions.GetSessionAsync(s.Owner, s.Id, ct);
            if (info?.PodIp is not { Length: > 0 } podIp || info.Phase != "Running") return "";
            var full = AgentTerminal.StripAnsi(await AgentTerminal.ReadScrollbackAsync(podIp, _agentPort, ct));
            var delta = full.Length > thread.PostedLen ? full[thread.PostedLen..] : "";
            await _threads.SetPostedLenAsync(s.Id, full.Length, ct);
            return delta.Trim();
        }
        catch (Exception ex) { _log.LogDebug(ex, "Could not read agent scrollback for {Id}", s.Id); return ""; }
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Trim(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}
